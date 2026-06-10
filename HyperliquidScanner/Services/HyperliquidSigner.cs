using System.Text;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Org.BouncyCastle.Crypto.Digests;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Handles Hyperliquid L1 action signing.
    ///
    /// Flow (matches Hyperliquid Python SDK signing.py):
    ///   1. Serialize action with msgpack
    ///   2. Append nonce (8 bytes big-endian) + vault flag (0x00)
    ///   3. keccak256 → connectionId
    ///   4. Build EIP-712 Agent typed data with connectionId
    ///   5. Sign digest with private key → (r, s, v)
    /// </summary>
    internal static class HyperliquidSigner
    {
        // ── EIP-712 domain (pre-computed at startup) ──────────────────────────

        private static readonly byte[] DomainTypeHash = KeccakRaw(
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");

        private static readonly byte[] AgentTypeHash = KeccakRaw(
            "Agent(string source,bytes32 connectionId)");

        private static readonly byte[] DomainSeparator = BuildDomainSeparator();

        private static byte[] BuildDomainSeparator()
        {
            // { name:"Exchange", version:"1", chainId:1337, verifyingContract:0x0...0 }
            var enc = DomainTypeHash
                .Concat(KeccakRaw("Exchange"))
                .Concat(KeccakRaw("1"))
                .Concat(PadUInt256(1337))
                .Concat(new byte[32])   // zero address
                .ToArray();
            return KeccakRaw(enc);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Signs a serialised action bytes blob and returns the (r, s, v) signature
        /// as 0x-prefixed hex strings, matching Hyperliquid's expected format.
        /// </summary>
        public static (string r, string s, int v) Sign(
            byte[] actionBytes, long nonce, string privateKey, bool isMainnet = true, string? vaultAddress = null)
        {
            // Step 1–3: build connectionId
            var nonceBytes = BitConverter.GetBytes((ulong)nonce);
            if (BitConverter.IsLittleEndian) Array.Reverse(nonceBytes);

            // Vault/sub-account flag: 0x00 if signing for own account, otherwise
            // 0x01 followed by the 20-byte vault/sub-account address.
            byte[] vaultBytes;
            if (string.IsNullOrWhiteSpace(vaultAddress))
            {
                vaultBytes = new byte[] { 0x00 };
            }
            else
            {
                var hex = vaultAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? vaultAddress[2..] : vaultAddress;
                var addrBytes = Enumerable.Range(0, hex.Length / 2)
                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                    .ToArray();
                vaultBytes = new byte[] { 0x01 }.Concat(addrBytes).ToArray();
            }

            var data         = actionBytes.Concat(nonceBytes).Concat(vaultBytes).ToArray();
            var connectionId = KeccakRaw(data);  // 32 bytes

            // Debug: log intermediate values to compare with Python SDK
            Serilog.Log.Debug("Sign debug: actionBytes={A} nonce={N} connectionId={C}",
                BitConverter.ToString(actionBytes).Replace("-","").ToLower(),
                nonce,
                BitConverter.ToString(connectionId).Replace("-","").ToLower());

            // Log domain separator (static, computed once)
            Serilog.Log.Debug("Sign debug: domainSep={D} agentTypeHash={T}",
                BitConverter.ToString(DomainSeparator).Replace("-","").ToLower(),
                BitConverter.ToString(AgentTypeHash).Replace("-","").ToLower());

            // Step 4: EIP-712 struct hash for Agent
            var source     = isMainnet ? "a" : "b";
            var structEnc  = AgentTypeHash
                .Concat(KeccakRaw(source))
                .Concat(connectionId)
                .ToArray();
            var structHash = KeccakRaw(structEnc);

            // Step 5: digest = keccak256("\x19\x01" + domainSeparator + structHash)
            var digest = KeccakRaw(
                new byte[] { 0x19, 0x01 }
                    .Concat(DomainSeparator)
                    .Concat(structHash)
                    .ToArray());

            Serilog.Log.Debug("Sign debug: structHash={SH} finalDigest={D}",
                BitConverter.ToString(structHash).Replace("-","").ToLower(),
                BitConverter.ToString(digest).Replace("-","").ToLower());

            // Step 6: sign
            var key = new EthECKey(privateKey);
            var sig = key.SignAndCalculateV(digest);

            return (
                "0x" + BitConverter.ToString(Pad32(sig.R)).Replace("-", "").ToLowerInvariant(),
                "0x" + BitConverter.ToString(Pad32(sig.S)).Replace("-", "").ToLowerInvariant(),
                sig.V[0]
            );
        }

        // ── Msgpack serialisation for order actions ───────────────────────────

        /// <summary>
        /// Serialises a trigger order (TP or SL) action to msgpack bytes.
        /// tpsl: "tp" for take profit, "sl" for stop loss.
        /// isMarket: true = market execution when triggered, false = limit.
        /// </summary>
        public static byte[] SerializeTriggerOrder(
            int assetIndex, bool isBuy, string price, string triggerPrice,
            string size, bool reduceOnly, string tpsl, bool isMarket)
        {
            var triggerMap = Map(
                ("isMarket",  Bool(isMarket)),
                ("triggerPx", Str(triggerPrice)),
                ("tpsl",      Str(tpsl))
            );
            var typeMap  = Map(("trigger", triggerMap));
            var order    = Map(
                ("a", Int(assetIndex)),
                ("b", Bool(isBuy)),
                ("p", Str(price)),
                ("s", Str(size)),
                ("r", Bool(reduceOnly)),
                ("t", typeMap)
            );
            return Map(
                ("type",     Str("order")),
                ("orders",   Arr(order)),
                ("grouping", Str("na"))
            );
        }

        /// <summary>Serialises a cancel action for a single order.</summary>
        public static byte[] SerializeCancelOrder(int assetIndex, long orderId)
        {
            var cancel = Map(
                ("a", Int(assetIndex)),
                ("o", Long(orderId))
            );
            return Map(
                ("type",    Str("cancel")),
                ("cancels", Arr(cancel))
            );
        }

        /// <summary>
        /// Serialises a limit order action to msgpack bytes.
        /// Maps field ordering must exactly match Hyperliquid's Python SDK.
        /// </summary>
        public static byte[] SerializeLimitOrder(
            int assetIndex, bool isBuy, string price, string size,
            bool reduceOnly, string tif = "Gtc")
        {
            // t: { limit: { tif: "Gtc" } }
            var tifMap    = Map(("tif",    Str(tif)));
            var limitMap  = Map(("limit",  tifMap));

            // single order entry
            var order = Map(
                ("a", Int(assetIndex)),
                ("b", Bool(isBuy)),
                ("p", Str(price)),
                ("s", Str(size)),
                ("r", Bool(reduceOnly)),
                ("t", limitMap)
            );

            // top-level action
            return Map(
                ("type",     Str("order")),
                ("orders",   Arr(order)),
                ("grouping", Str("na"))
            );
        }

        // ── Minimal msgpack encoder ───────────────────────────────────────────
        //
        // Only encodes the types needed for Hyperliquid order actions.
        // Supports: fixmap, fixarray, fixstr/str8, fixint/uint8/uint16, bool.

        private static byte[] Map(params (string key, byte[] value)[] entries)
        {
            var result = new List<byte> { (byte)(0x80 | entries.Length) };
            foreach (var (k, v) in entries) { result.AddRange(Str(k)); result.AddRange(v); }
            return result.ToArray();
        }

        private static byte[] Arr(params byte[][] items)
        {
            var result = new List<byte> { (byte)(0x90 | items.Length) };
            foreach (var item in items) result.AddRange(item);
            return result.ToArray();
        }

        private static byte[] Str(string s)
        {
            var bytes  = Encoding.UTF8.GetBytes(s);
            var result = new List<byte>();
            if (bytes.Length <= 31)
                result.Add((byte)(0xa0 | bytes.Length));
            else { result.Add(0xd9); result.Add((byte)bytes.Length); }
            result.AddRange(bytes);
            return result.ToArray();
        }

        private static byte[] Bool(bool v) => new[] { v ? (byte)0xc3 : (byte)0xc2 };

        private static byte[] Int(int v)
        {
            if (v is >= 0 and <= 127) return new[] { (byte)v };
            if (v <= 255)             return new[] { (byte)0xcc, (byte)v };
            return new[] { (byte)0xcd, (byte)(v >> 8), (byte)(v & 0xff) };
        }

        private static byte[] Long(long v)
        {
            if (v is >= 0 and <= 127) return new[] { (byte)v };
            // uint64: 0xcf + 8 bytes big-endian
            var bytes = BitConverter.GetBytes((ulong)v);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return new byte[] { 0xcf }.Concat(bytes).ToArray();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static byte[] KeccakRaw(byte[] data)
        {
            var digest = new KeccakDigest(256);
            digest.BlockUpdate(data, 0, data.Length);
            var result = new byte[32];
            digest.DoFinal(result, 0);
            return result;
        }

        private static byte[] KeccakRaw(string utf8) =>
            KeccakRaw(Encoding.UTF8.GetBytes(utf8));

        private static byte[] PadUInt256(long value)
        {
            var result = new byte[32];
            var bytes  = BitConverter.GetBytes((ulong)value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, result, 32 - bytes.Length, bytes.Length);
            return result;
        }

        private static byte[] Pad32(byte[] bytes)
        {
            if (bytes.Length == 32) return bytes;
            var result = new byte[32];
            Array.Copy(bytes, 0, result, 32 - bytes.Length, bytes.Length);
            return result;
        }
    }
}
