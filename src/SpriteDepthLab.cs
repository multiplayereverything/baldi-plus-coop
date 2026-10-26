using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        bool spriteDepthInspected;
        void UpdateSpriteDepthLab()
        {
            if (!Lab.Enabled || spriteDepthInspected || !TitleVisible || !(Lab.HasArg("-bt-sprite-depth") || Lab.HasArg("-bt-sprite-stack"))) return;
            spriteDepthInspected=true;
            var report=new StringBuilder();
            foreach(var npc in Resources.FindObjectsOfTypeAll<NPC>().Where(n=>n is Baldi || n is Playtime || n is Principal))
                foreach(var renderer in npc.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    var mat=renderer.sharedMaterial;if(mat==null)continue;
                    report.AppendLine(npc.name+" / "+renderer.name+" / "+mat.name+" / shader="+mat.shader.name+" / layer="+renderer.gameObject.layer+" / queue="+mat.renderQueue+" / sort="+renderer.sortingLayerID+","+renderer.sortingOrder+" / center="+renderer.spriteSortPoint);
                    for(int i=0;i<mat.shader.GetPropertyCount();i++)
                    {
                        string key=mat.shader.GetPropertyName(i);var type=mat.shader.GetPropertyType(i);
                        if(type==UnityEngine.Rendering.ShaderPropertyType.Float || type==UnityEngine.Rendering.ShaderPropertyType.Range)report.AppendLine(key+"="+mat.GetFloat(key));
                        else if(type==UnityEngine.Rendering.ShaderPropertyType.Texture){var tex=mat.GetTexture(key);report.AppendLine(key+"="+(tex==null?"null":tex.name));}
                    }
                }
            report.AppendLine("Available shaders: "+string.Join(",",Resources.FindObjectsOfTypeAll<Shader>().Where(s=>s.name.IndexOf("Sprite",StringComparison.OrdinalIgnoreCase)>=0).Select(s=>s.name).Distinct().ToArray()));
            foreach(var gameCamera in Resources.FindObjectsOfTypeAll<GameCamera>())
            {
                foreach(var cam in new[]{gameCamera.camCom,gameCamera.billboardCam})if(cam!=null)
                {report.AppendLine("CAM "+cam.name+" mask="+cam.cullingMask+" clear="+cam.clearFlags);foreach(var component in cam.GetComponents<Component>())if(component!=null&&component.GetType().Name=="UniversalAdditionalCameraData") {var prop=component.GetType().GetProperty("clearDepth");report.AppendLine("URP clearDepth="+(prop==null?"?":prop.GetValue(component,null).ToString()));}}
            }
            File.WriteAllText(Path.Combine(Root,"MultiplayerMod/private/sprite-materials.txt"),report.ToString());
            Log("SPRITE DEPTH: material inspection saved.");
            if(Lab.HasArg("-bt-sprite-stack"))RunSpriteStackDepthCheck();else RunSpriteDepthCheck();
        }

        static float PixelDifference(Color a,Color b) { return Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b))); }

        void RunSpriteStackDepthCheck()
        {
            PlayerSpriteSheet.EnsureMaterial();
            var rig=Resources.FindObjectsOfTypeAll<GameCamera>().First(c=>c.camCom!=null&&c.billboardCam!=null);
            var source=Resources.FindObjectsOfTypeAll<Baldi>().SelectMany(n=>n.GetComponentsInChildren<SpriteRenderer>(true)).First(r=>r.sprite!=null&&r.sharedMaterial!=null);
            var avatar=new GameObject("Stack test remote player");avatar.transform.position=new Vector3(10000,5,0);avatar.AddComponent<RemotePlayerSprite>();
            var player=avatar.GetComponentInChildren<SpriteRenderer>();
            var npcObject=new GameObject("Stack test NPC",typeof(SpriteRenderer));var npc=npcObject.GetComponent<SpriteRenderer>();npc.sprite=source.sprite;npcObject.layer=source.gameObject.layer;
            var playerMaterial=new Material(player.sharedMaterial);playerMaterial.SetFloat("_ColorMultiplier",20);player.sharedMaterial=playerMaterial;
            var npcMaterial=new Material(source.sharedMaterial);npcMaterial.SetFloat("_ColorMultiplier",20);npc.sharedMaterial=npcMaterial;
            var mainObject=new GameObject("Stack test world camera",typeof(Camera));var main=mainObject.GetComponent<Camera>();main.enabled=false;main.fieldOfView=60;main.nearClipPlane=.1f;main.farClipPlane=100;
            main.transform.position=new Vector3(10000,5,-16);main.transform.rotation=Quaternion.Euler(8,0,0);main.clearFlags=CameraClearFlags.SolidColor;main.backgroundColor=Color.magenta;main.cullingMask=rig.camCom.cullingMask;
            var overlayObject=new GameObject("Stack test billboard camera",typeof(Camera));var overlay=overlayObject.GetComponent<Camera>();overlay.CopyFrom(main);overlay.enabled=false;overlay.transform.SetPositionAndRotation(main.transform.position,main.transform.rotation);
            overlay.cullingMask=rig.billboardCam.cullingMask;overlay.clearFlags=rig.billboardCam.clearFlags;
            var target=new RenderTexture(256,256,24);main.targetTexture=overlay.targetTexture=target;
            string directory=Path.Combine(Root,"MultiplayerMod/private/captures");Directory.CreateDirectory(directory);
            var checks=new System.Collections.Generic.Dictionary<string,bool>();
            checks["native-camera-routing"]=player.gameObject.layer==source.gameObject.layer&&(main.cullingMask&(1<<player.gameObject.layer))==0&&(overlay.cullingMask&(1<<player.gameObject.layer))!=0;
            try
            {
                foreach(bool fixedLayer in new[]{false,true})
                for(int front=0;front<2;front++)
                {
                    player.gameObject.layer=fixedLayer?PlayerSpriteSheet.RenderLayer:0;
                    avatar.transform.position=new Vector3(10000,5,front==0?6:0);
                    npc.transform.position=new Vector3(10000,-npc.sprite.bounds.min.y,front==0?0:6);
                    player.enabled=true;npc.enabled=false;main.Render();var playerOnly=DepthPixels(overlay,target,null);
                    player.enabled=false;npc.enabled=true;main.Render();var npcOnly=DepthPixels(overlay,target,null);
                    player.enabled=true;main.Render();string prefix=fixedLayer?"fixed":"legacy";var combined=DepthPixels(overlay,target,Path.Combine(directory,"stack-"+prefix+"-"+front+".png"));
                    int overlap=0,correct=0,holes=0,clear=0;
                    for(int i=0;i<combined.Length;i++)
                    {
                        bool p=PixelDifference(playerOnly[i],Color.magenta)>.05f,n=PixelDifference(npcOnly[i],Color.magenta)>.05f;
                        if(p&&n&&PixelDifference(playerOnly[i],npcOnly[i])>.15f){overlap++;if(PixelDifference(combined[i],front==0?npcOnly[i]:playerOnly[i])<.03f)correct++;}
                        if(!p&&n){holes++;if(PixelDifference(combined[i],npcOnly[i])<.03f)clear++;}
                    }
                    bool right=overlap>100&&correct>=overlap*.98f;
                    if(fixedLayer){checks[front==0?"npc-front":"player-front"]=right;if(front==1)checks["transparent-holes"]=holes>10&&clear>=holes*.98f;}
                    else if(front==1)checks["legacy-regression-reproduced"]=!right&&overlap>100;
                    Log("STACK "+prefix+" front="+front+" pixels="+correct+"/"+overlap+" holes="+clear+"/"+holes);
                }
                int passed=checks.Count(p=>p.Value);string json="{\"passed\":"+passed+",\"failed\":"+(checks.Count-passed)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
                string tests=Path.Combine(Root,"MultiplayerMod/artifacts/tests");Directory.CreateDirectory(tests);File.WriteAllText(Path.Combine(tests,"sprite-stack-results.json"),json);Log("STACK results "+json);
            }
            finally {main.targetTexture=overlay.targetTexture=null;target.Release();Destroy(target);Destroy(avatar);Destroy(npcObject);Destroy(mainObject);Destroy(overlayObject);Destroy(playerMaterial);Destroy(npcMaterial);}
        }

        Color[] DepthPixels(Camera camera,RenderTexture target,string path)
        {
            var old=RenderTexture.active;Texture2D image=null;
            try {camera.Render();RenderTexture.active=target;image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();if(path!=null)File.WriteAllBytes(path,image.EncodeToPNG());return image.GetPixels();}
            finally {RenderTexture.active=old;if(image!=null)Destroy(image);}
        }

        void RunSpriteDepthCheck()
        {
            PlayerSpriteSheet.EnsureMaterial();
            var source=Resources.FindObjectsOfTypeAll<NPC>().Where(n=>n is Baldi).SelectMany(n=>n.GetComponentsInChildren<SpriteRenderer>(true)).First(r=>r.sprite!=null && r.sharedMaterial!=null);
            var candidate=new Material(PlayerSpriteSheet.Material);candidate.name="Player depth candidate";
            // Title scenes have no level light map; amplify both fixture materials equally.
            candidate.SetFloat("_ColorMultiplier",20);
            var npcMaterial=new Material(source.sharedMaterial);npcMaterial.SetFloat("_ColorMultiplier",20);
            var legacy=new Material(Shader.Find("Sprites/Default"));
            var cameraObject=new GameObject("Sprite depth test camera");var camera=cameraObject.AddComponent<Camera>();
            camera.enabled=false;camera.orthographic=true;camera.orthographicSize=5;camera.transform.position=new Vector3(0,0,-20);
            camera.nearClipPlane=.1f;camera.farClipPlane=100;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.magenta;camera.cullingMask=1<<30;
            var target=new RenderTexture(256,256,24);camera.targetTexture=target;
            var a=new GameObject("Player test",typeof(SpriteRenderer));a.layer=30;var player=a.GetComponent<SpriteRenderer>();player.sprite=PlayerSpriteSheet.Sprites[0];
            var b=new GameObject("Baldi test",typeof(SpriteRenderer));b.layer=30;var npc=b.GetComponent<SpriteRenderer>();npc.sprite=source.sprite;npc.sharedMaterial=npcMaterial;
            player.sortingLayerID=npc.sortingLayerID=source.sortingLayerID;player.sortingOrder=npc.sortingOrder=source.sortingOrder;
            string directory=Path.Combine(Root,"MultiplayerMod/private/captures");Directory.CreateDirectory(directory);
            var results=new System.Collections.Generic.Dictionary<string,bool>();
            try
            {
                foreach(bool native in new[]{false,true})
                {
                    string prefix=native?"native":"default";player.sharedMaterial=native?candidate:legacy;
                    player.transform.localScale=Vector3.one*(8/player.sprite.bounds.size.y);npc.transform.localScale=Vector3.one*(8/npc.sprite.bounds.size.y);
                    player.transform.position=-player.sprite.bounds.center*player.transform.localScale.y;npc.transform.position=-npc.sprite.bounds.center*npc.transform.localScale.y;
                    npc.enabled=false;player.enabled=true;var playerOnly=DepthPixels(camera,target,null);
                    npc.enabled=true;player.enabled=false;var npcOnly=DepthPixels(camera,target,null);
                    player.enabled=true;player.transform.position+=Vector3.forward*5;var npcFront=DepthPixels(camera,target,Path.Combine(directory,"depth-"+prefix+"-npc-front.png"));
                    player.transform.position-=Vector3.forward*5;npc.transform.position+=Vector3.forward*5;var playerFront=DepthPixels(camera,target,Path.Combine(directory,"depth-"+prefix+"-player-front.png"));
                    player.enabled=false;var npcBehind=DepthPixels(camera,target,null);player.enabled=true;
                    int overlap=0,npcCorrect=0,playerCorrect=0,holes=0,holesCorrect=0;
                    for(int i=0;i<playerOnly.Length;i++)
                    {
                        bool p=PixelDifference(playerOnly[i],Color.magenta)>.05f,n=PixelDifference(npcOnly[i],Color.magenta)>.05f;
                        if(p&&n&&PixelDifference(playerOnly[i],npcOnly[i])>.15f)
                        {overlap++;if(PixelDifference(npcFront[i],npcOnly[i])<.03f)npcCorrect++;if(PixelDifference(playerFront[i],playerOnly[i])<.03f)playerCorrect++;}
                        if(!p&&PixelDifference(npcBehind[i],Color.magenta)>.05f){holes++;if(PixelDifference(playerFront[i],npcBehind[i])<.03f)holesCorrect++;}
                    }
                    results[prefix+"-npc-front"]=overlap>100&&npcCorrect>=overlap*.98f;
                    results[prefix+"-player-front"]=overlap>100&&playerCorrect>=overlap*.98f;
                    results[prefix+"-transparent"]=holes>10&&holesCorrect>=holes*.98f;
                    Log("DEPTH "+prefix+": overlap="+overlap+" NPC="+npcCorrect+" player="+playerCorrect+" holes="+holesCorrect+"/"+holes+" shader="+player.sharedMaterial.shader.name);
                }
                string json="{"+string.Join(",",results.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}";
                File.WriteAllText(Path.Combine(Root,"MultiplayerMod/private/depth-comparison.json"),json);Log("DEPTH comparison "+json);
                bool regression=!results["default-player-front"];
                int passed=(regression?1:0)+results.Count(p=>p.Key.StartsWith("native-")&&p.Value);
                string tests=Path.Combine(Root,"MultiplayerMod/artifacts/tests");Directory.CreateDirectory(tests);
                File.WriteAllText(Path.Combine(tests,"depth-results.json"),"{\"passed\":"+passed+",\"failed\":"+(4-passed)+",\"legacyRegressionReproduced\":"+regression.ToString().ToLowerInvariant()+",\"checks\":"+json+"}");
            }
            finally {camera.targetTexture=null;target.Release();Destroy(target);Destroy(a);Destroy(b);Destroy(cameraObject);Destroy(candidate);Destroy(npcMaterial);Destroy(legacy);}
        }
    }
}
