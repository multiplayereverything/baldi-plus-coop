using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class VendDelivery { public int Buyer; public string Key; public float Deadline; }
        readonly Dictionary<string,SodaMachine> vending = new Dictionary<string,SodaMachine>();
        readonly Dictionary<int,VendDelivery> vendingDeliveries = new Dictionary<int,VendDelivery>();
        readonly HashSet<int> vendingReceipts = new HashSet<int>();
        readonly Dictionary<int,int> vendingRequests = new Dictionary<int,int>();
        readonly Dictionary<SodaMachine,Material[]> vendingOriginalMaterials = new Dictionary<SodaMachine,Material[]>();
        int vendingSerial, vendingRequest, vendingSlot; bool vendingPending, vendingReplica; float vendingDeadline;
        static T VendField<T>(SodaMachine machine,string name) { return (T)AccessTools.Field(typeof(SodaMachine),name).GetValue(machine); }
        void ResetVending() { vending.Clear();vendingOriginalMaterials.Clear();vendingDeliveries.Clear();vendingReceipts.Clear();vendingRequests.Clear();vendingPending=false;vendingReplica=false; }
        // Some rooms place two machines that round to the same key (seed 2101520539 has two at 95,0,65), which used to
        // fail the whole level check. Machines are registered in stable hierarchy order and a repeat key gets a #n
        // suffix; host and clients generate the same level, so they number the same machines the same way.
        static string HierarchyOrder(Transform t) { string path = t.GetSiblingIndex().ToString("D4") + t.name; while (t.parent != null) { t = t.parent; path = t.GetSiblingIndex().ToString("D4") + t.name + "/" + path; } return path; }
        int vendingDuplicateKeys;
        string VendingKey(SodaMachine machine) { foreach (var pair in vending) if (pair.Value == machine) return pair.Key; return Key(machine); }
        void BuildVendingWorld() { ResetVending();foreach(var machine in Object.FindObjectsOfType<SodaMachine>(true).OrderBy(m=>Key(m),StringComparer.Ordinal).ThenBy(m=>HierarchyOrder(m.transform),StringComparer.Ordinal)){string key=Key(machine);if(vending.ContainsKey(key)){vendingDuplicateKeys++;Log("Vending: two machines share "+key+" ("+machine.name+"); numbering the repeat.");int n=2;while(vending.ContainsKey(key+"#"+n))n++;key=key+"#"+n;}vending.Add(key,machine);var renderer=VendField<MeshRenderer>(machine,"meshRenderer");if(renderer!=null)vendingOriginalMaterials[machine]=renderer.sharedMaterials;}if(vending.Count>512)throw new IOException("Too many vending machines."); }
        void SetVendingStock(SodaMachine machine,int stock)
        {
            AccessTools.Field(typeof(SodaMachine),"usesLeft").SetValue(machine,stock);
            var renderer=VendField<MeshRenderer>(machine,"meshRenderer");var material=VendField<Material>(machine,"outOfStockMat");
            Material[] original;if(stock>0){if(renderer!=null&&vendingOriginalMaterials.TryGetValue(machine,out original))renderer.sharedMaterials=original;return;}
            if(renderer!=null&&material!=null){var mats=renderer.sharedMaterials;if(mats.Length>1){mats[1]=material;renderer.sharedMaterials=mats;}}
        }
        public bool BeforeVendingUse(ItemManager inventory)
        {
            if(!Active||Applying||!worldReady)return true;
            if(PanelOpen)return false;
            var pm=Singleton<CoreGameManager>.Instance.GetPlayer(0);
            int slot=inventory.selectedItem;
            if(pm==null||pm.itm!=inventory||slot<0||slot>=inventory.items.Length||inventory.items[slot].itemType!=Items.Quarter)return true;
            if((bool)AccessTools.Field(typeof(ItemManager),"disabled").GetValue(inventory)&&(!inventory.items[slot].overrideDisabled||inventory.maxItem<0))return true;
            RaycastHit hit;
            if(!Physics.Raycast(pm.transform.position,Singleton<CoreGameManager>.Instance.GetCamera(pm.playerNumber).transform.forward,out hit,pm.pc.Reach,pm.pc.ClickLayers))return true;
            var machine=hit.transform.GetComponent<SodaMachine>();if(machine==null)return true;
            if(LocalEliminated||vendingPending||!machine.ItemFits(Items.Quarter)||(!Hosting&&!vendingReplica))return false;
            if(Hosting){bool previous=Applying;Applying=true;try{inventory.RemoveItem(slot);machine.InsertItem(pm,pm.ec);}finally{Applying=previous;}return false;}
            if(!readyAcknowledged)return false;
            vendingSlot=slot;vendingPending=true;vendingDeadline=Time.realtimeSinceStartup+10;
            Network.Send(0,Wire.Packet(Op.VendingRequest,w=>{w.Write(epoch);w.Write(++vendingRequest);Wire.Text(w,VendingKey(machine));}));
            return false;
        }
        bool HandleVendingClient(int id,Peer peer,Op op,BinaryReader r)
        {
            if(op==Op.VendingRequest)
            {
                int generation=r.ReadInt32(),request=r.ReadInt32();string key=Wire.Text(r,256);
                int previous; if(generation!=epoch||!worldReady)return true;
                if(vendingRequests.TryGetValue(id,out previous)&&request<=previous)return true;vendingRequests[id]=request;
                SodaMachine machine;vending.TryGetValue(key,out machine);
                bool valid=peer.Ready&&peer.HasPose&&!peer.Eliminated&&!peer.InLocker&&!vendingDeliveries.Values.Any(d=>d.Buyer==id);
                valid=valid&&machine!=null&&machine.gameObject.activeInHierarchy&&machine.ItemFits(Items.Quarter)&&Time.realtimeSinceStartup-peer.PoseAt<2;
                if(valid)
                {
                    var player=Singleton<CoreGameManager>.Instance.GetPlayer(0);float reach=player.pc.Reach;
                    Vector3 delta=machine.transform.position-peer.Position;RaycastHit hit;
                    valid=delta.magnitude<=reach+2&&Physics.Raycast(peer.Position,delta.normalized,out hit,reach+2,player.pc.ClickLayers)&&hit.transform.GetComponent<SodaMachine>()==machine;
                }
                string item="";int token=0;
                if(valid)
                {
                    var choices=VendField<WeightedItemObject[]>(machine,"potentialItems");
                    ItemObject selected=choices.Length==0?VendField<ItemObject>(machine,"item"):WeightedSelection<ItemObject>.RandomSelection(choices);
                    if(selected!=null){item=selected.name;token=++vendingSerial;SetVendingStock(machine,VendField<int>(machine,"usesLeft")-1);vendingDeliveries[token]=new VendDelivery{Buyer=id,Key=key,Deadline=Time.realtimeSinceStartup+12};}
                }
                Network.Send(id,Wire.Packet(Op.VendingReply,w=>{w.Write(epoch);w.Write(request);w.Write(token);Wire.Text(w,item);}));return true;
            }
            if(op==Op.VendingAck)
            {
                int generation=r.ReadInt32(),token=r.ReadInt32();bool delivered=r.ReadBoolean();VendDelivery delivery;
                if(generation==epoch&&vendingDeliveries.TryGetValue(token,out delivery)&&delivery.Buyer==id)
                {
                    vendingDeliveries.Remove(token);
                    // Explicit failure is safe to restore; unknown delivery on disconnect stays consumed.
                    SodaMachine machine;if(!delivered&&vending.TryGetValue(delivery.Key,out machine))SetVendingStock(machine,VendField<int>(machine,"usesLeft")+1);
                }
                return true;
            }
            return false;
        }
        bool HandleVendingHost(Op op,BinaryReader r)
        {
            if(op!=Op.VendingReply)return false;
            int generation=r.ReadInt32(),request=r.ReadInt32(),token=r.ReadInt32();string item=Wire.Text(r,128);
            if(generation!=epoch)return true;
            if(token==0){if(request==vendingRequest){vendingPending=false;Notify("That vending machine is unavailable.");}return true;}
            bool delivered=vendingReceipts.Contains(token);
            if(!delivered&&request==vendingRequest&&vendingPending&&Time.realtimeSinceStartup<=vendingDeadline&&worldReady&&!LocalEliminated)
            {
                var inventory=Singleton<CoreGameManager>.Instance.GetPlayer(0).itm;
                if(vendingSlot>=0&&vendingSlot<inventory.items.Length&&inventory.items[vendingSlot].itemType==Items.Quarter)
                {
                    var asset=FindShopAsset(item);bool previous=Applying;Applying=true;
                    try{inventory.RemoveItem(vendingSlot);inventory.AddItem(asset);vendingReceipts.Add(token);delivered=true;}finally{Applying=previous;}
                }
            }
            if(request==vendingRequest)vendingPending=false;Network.Send(0,Wire.Packet(Op.VendingAck,w=>{w.Write(epoch);w.Write(token);w.Write(delivered);}));return true;
        }
        void WriteVendingWorld(BinaryWriter w) { w.Write((ushort)vending.Count);foreach(var pair in vending){Wire.Text(w,pair.Key);w.Write(VendField<int>(pair.Value,"usesLeft"));} }
        void ReadVendingWorld(BinaryReader r,bool apply)
        {
            int count=r.ReadUInt16();if(count>512)throw new IOException("Too many vending machines.");
            for(int i=0;i<count;i++){string key=Wire.Text(r,256);int stock=r.ReadInt32();if(stock<0||stock>100000)throw new IOException("Invalid vending stock.");SodaMachine machine;if(apply&&vending.TryGetValue(key,out machine))SetVendingStock(machine,stock);}
            if(apply)vendingReplica=true;
        }
        bool vendingLabDone;
        bool vendingNativeStarted;
        void UpdateVendingLab()
        {
            if(vendingPending&&Time.realtimeSinceStartup>vendingDeadline){vendingPending=false;Notify("Vending request timed out. Your quarter was kept.");}
            if(Hosting)foreach(var pair in vendingDeliveries.Where(p=>p.Value.Deadline>0&&Time.realtimeSinceStartup>p.Value.Deadline).ToArray())vendingDeliveries.Remove(pair.Key);
            if(!vendingNativeStarted&&Lab.Enabled&&TitleVisible&&Lab.HasArg("-bt-vending-native-tour")){vendingNativeStarted=true;StartCoroutine(TourNativeVending());}
            if(vendingLabDone||!Lab.Enabled||!Lab.HasArg("-bt-vending-tour"))return;
            vendingLabDone=true;var checks=new Dictionary<string,bool>();
            var go=new GameObject("Vending test");var machine=go.AddComponent<SodaMachine>();
            var quarter=ScriptableObject.CreateInstance<ItemObject>();quarter.itemType=Items.Quarter;
            AccessTools.Field(typeof(SodaMachine),"requiredItem").SetValue(machine,quarter);
            SetVendingStock(machine,2);checks["native-quarter-fits"]=machine.ItemFits(Items.Quarter);checks["native-wrong-item-rejected"]=!machine.ItemFits(Items.None);
            SetVendingStock(machine,0);checks["native-empty-rejected"]=!machine.ItemFits(Items.Quarter);
            SetVendingStock(machine,1);checks["stock-restores"]=machine.ItemFits(Items.Quarter);
            vending["lab-vending"]=machine;
            using(var stream=new MemoryStream()){using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))WriteVendingWorld(writer);SetVendingStock(machine,0);stream.Position=0;using(var reader=new BinaryReader(stream))ReadVendingWorld(reader,true);checks["stock-roundtrip"]=VendField<int>(machine,"usesLeft")==1&&vendingReplica;}
            var oldNetwork=Network;bool oldReady=worldReady;Network=new Transport();worldReady=true;
            var peer=new Peer{Id=3,Ready=true,HasPose=true,InLocker=true,PoseAt=Time.realtimeSinceStartup};
            Action<int,int,string> requestPacket=(generation,request,key)=>
            {
                using(var stream=new MemoryStream())
                {using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){writer.Write(generation);writer.Write(request);Wire.Text(writer,key);}stream.Position=0;using(var reader=new BinaryReader(stream))HandleVendingClient(3,peer,Op.VendingRequest,reader);}
            };
            Action<int,int,bool> ackPacket=(buyer,token,delivered)=>
            {
                using(var stream=new MemoryStream())
                {using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){writer.Write(epoch);writer.Write(token);writer.Write(delivered);}stream.Position=0;using(var reader=new BinaryReader(stream))HandleVendingClient(buyer,peer,Op.VendingAck,reader);}
            };
            requestPacket(epoch-1,10,"lab-vending");checks["stale-request-ignored"]=!vendingRequests.ContainsKey(3);
            requestPacket(epoch,10,"lab-vending");checks["locker-request-denied"]=vendingRequests[3]==10&&VendField<int>(machine,"usesLeft")==1&&vendingDeliveries.Count==0;
            requestPacket(epoch,9,"missing");checks["request-replay-rejected"]=vendingRequests[3]==10;
            peer.InLocker=false;peer.Eliminated=true;requestPacket(epoch,11,"lab-vending");checks["eliminated-request-denied"]=VendField<int>(machine,"usesLeft")==1&&vendingDeliveries.Count==0;
            SetVendingStock(machine,0);vendingDeliveries[10000]=new VendDelivery{Buyer=3,Key="lab-vending"};
            ackPacket(2,10000,false);checks["wrong-buyer-ack-rejected"]=vendingDeliveries.ContainsKey(10000)&&VendField<int>(machine,"usesLeft")==0;
            ackPacket(3,10000,false);checks["failed-delivery-refunds-stock"]=!vendingDeliveries.ContainsKey(10000)&&VendField<int>(machine,"usesLeft")==1;
            ackPacket(3,10000,false);checks["duplicate-ack-no-double-refund"]=VendField<int>(machine,"usesLeft")==1;
            Network.Dispose();Network=oldNetwork;worldReady=oldReady;vendingRequests.Remove(3);
            vending.Remove("lab-vending");Object.Destroy(go);Object.Destroy(quarter);
            var path=Path.Combine(Root,"MultiplayerMod/artifacts/tests/vending-results.json");Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,"{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}");
        }
        System.Collections.IEnumerator TourNativeVending()
        {
            portText="27039";Connect(true);AutoStartRun();
            while(!worldReady)yield return null;
            yield return new WaitForSecondsRealtime(2);
            var checks=new Dictionary<string,bool>();var player=Singleton<CoreGameManager>.Instance.GetPlayer(0);var inventory=player.itm;
            var quarter=Resources.FindObjectsOfTypeAll<ItemObject>().First(i=>i.itemType==Items.Quarter);
            var reward=Resources.FindObjectsOfTypeAll<ItemObject>().First(i=>i.itemType==Items.Bsoda);
            for(int i=0;i<=inventory.maxItem;i++)inventory.RemoveItem(i);
            inventory.selectedItem=0;inventory.AddItem(quarter);vendingSlot=0;
            var prefab=Resources.FindObjectsOfTypeAll<SodaMachine>().First();
            var go=new GameObject("Native vending transaction lab");go.layer=prefab.gameObject.layer;
            var machine=go.AddComponent<SodaMachine>();var collider=go.AddComponent<BoxCollider>();collider.size=Vector3.one;
            go.transform.position=player.transform.position+Vector3.forward*3;
            AccessTools.Field(typeof(SodaMachine),"requiredItem").SetValue(machine,quarter);
            AccessTools.Field(typeof(SodaMachine),"item").SetValue(machine,reward);
            AccessTools.Field(typeof(SodaMachine),"potentialItems").SetValue(machine,new WeightedItemObject[0]);
            SetVendingStock(machine,2);vending["native-vend-lab"]=machine;Physics.SyncTransforms();
            var peer=new Peer{Id=3,Ready=true,HasPose=true,PoseAt=Time.realtimeSinceStartup,Position=player.transform.position};
            Action request=()=>{using(var stream=new MemoryStream()){using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){writer.Write(epoch);writer.Write(100);Wire.Text(writer,"native-vend-lab");}stream.Position=0;using(var reader=new BinaryReader(stream))HandleVendingClient(3,peer,Op.VendingRequest,reader);}};
            request();checks["host-reserves-one-stock"]=VendField<int>(machine,"usesLeft")==1&&vendingDeliveries.Values.Any(d=>d.Key=="native-vend-lab");
            request();checks["duplicate-request-no-extra-stock"]=VendField<int>(machine,"usesLeft")==1;
            int token=vendingDeliveries.Where(p=>p.Value.Key=="native-vend-lab").Select(p=>p.Key).FirstOrDefault();
            vendingRequest=100;vendingPending=true;vendingDeadline=Time.realtimeSinceStartup+10;
            Action reply=()=>{using(var stream=new MemoryStream()){using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){writer.Write(epoch);writer.Write(100);writer.Write(token);Wire.Text(writer,reward.name);}stream.Position=0;using(var reader=new BinaryReader(stream))HandleVendingHost(Op.VendingReply,reader);}};
            if(token!=0){reply();checks["recipient-quarter-consumed"]=!inventory.items.Any(i=>i.itemType==Items.Quarter);checks["recipient-reward-once"]=inventory.items.Count(i=>i.itemType==Items.Bsoda)==1;reply();checks["duplicate-grant-no-extra-reward"]=inventory.items.Count(i=>i.itemType==Items.Bsoda)==1;}
            else{checks["recipient-quarter-consumed"]=false;checks["recipient-reward-once"]=false;checks["duplicate-grant-no-extra-reward"]=false;}
            vending.Remove("native-vend-lab");vendingRequests.Remove(3);if(token!=0)vendingDeliveries.Remove(token);Object.Destroy(go);
            var path=Path.Combine(Root,"MultiplayerMod/artifacts/tests/vending-native-results.json");Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,"{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}");
        }
    }
    [HarmonyPatch(typeof(ItemManager),"UseItem")]
    static class VendingUsePatch { static bool Prefix(ItemManager __instance){var runtime=ModHooks.Current;return runtime==null||runtime.BeforeVendingUse(__instance);} }
}
