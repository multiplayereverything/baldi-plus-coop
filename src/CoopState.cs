using System.Collections.Generic;
using System.Linq;

namespace BaldiTogether
{
    // One outstanding grant per player; committed pickups stay claimed for this level.
    public sealed class SupplyClaims
    {
        sealed class Claim { public string Key; public int Owner, Token; public bool Settled; }
        readonly Dictionary<string, Claim> claims = new Dictionary<string, Claim>();
        readonly Dictionary<int, Claim> pending = new Dictionary<int, Claim>();
        int nextToken;
        public bool IsClaimed(string key) { return claims.ContainsKey(key); }
        public bool TryClaim(string key, int owner, out int token)
        {
            token = 0;
            if (claims.ContainsKey(key) || pending.Values.Any(c => c.Owner == owner)) return false;
            Claim claim = new Claim { Key = key, Owner = owner, Token = ++nextToken };
            claims.Add(key, claim); pending.Add(claim.Token, claim); token = claim.Token; return true;
        }
        public bool Resolve(int token, int owner, bool delivered, out string key)
        {
            key = null; Claim claim;
            if (!pending.TryGetValue(token, out claim) || claim.Owner != owner || claim.Settled) return false;
            key = claim.Key; pending.Remove(token); claim.Settled = true;
            if (!delivered) claims.Remove(claim.Key);
            return true;
        }
    }
}
