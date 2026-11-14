using System.Security.Cryptography;
using System.Text;

namespace PMKUnlocker;

// MobileSea temp-root token (payload ctor verify ကနေ reverse):
//   h   = FNV-1a64(serial)  seed 0x4d54560124e3892f  prime 0x100000001b3
//   m   = (h>>23) ^ (h<<17) ^ h
//   A   = m^K9 · B = (m^K10)+(A<<13) · C = (m^K11)-(A>>7) · D = K12-(B^C) · G = (D+C)^0xf0f0f0f0f0f0f0f
//   key = LE32(B||C||D||G)   iv = LE16((G^B)||(D^C))
//   pt  = 0x84 00 00 00 | epoch_le32 (time() ±120s) | 0x80a78f8aff7f8b8f_le64
//   token = hex( AES256_ECB( pt ^ iv ) )   — payload က AES_CBC_decrypt_buffer(iv pre-xor) နဲ့ decrypt
public static class MstToken
{
    private const ulong FnvSeed = 0x4d54560124e3892fUL;
    private const ulong K9  = 0xf44483726d495c8bUL;
    private const ulong K10 = 0xbfb02962b4792940UL;
    private const ulong K11 = 0xc90e702c1d2dafb6UL;
    private const ulong K12 = 0x7c32c93593f7375dUL;
    private const ulong FoldK = 0x80a78f8aff7f8b8fUL;

    /// <summary>serial (ro.serialno) + device epoch → 32-hex token</summary>
    public static string Build(string serial, long epoch)
    {
        byte[] pt = new byte[16];
        pt[0] = 0x84;
        uint e = (uint)epoch;
        pt[4] = (byte)e; pt[5] = (byte)(e >> 8); pt[6] = (byte)(e >> 16); pt[7] = (byte)(e >> 24);
        ulong k = FoldK;
        for (int i = 0; i < 8; i++) pt[8 + i] = (byte)(k >> (8 * i));

        Derive(serial, out byte[] key, out byte[] iv);
        for (int i = 0; i < 16; i++) pt[i] ^= iv[i];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        byte[] ct = aes.CreateEncryptor().TransformFinalBlock(pt, 0, 16);
        return Convert.ToHexString(ct).ToLowerInvariant();
    }

    public static void Derive(string serial, out byte[] key, out byte[] iv)
    {
        ulong h = FnvSeed;
        foreach (byte b in Encoding.ASCII.GetBytes(serial ?? ""))
            h = (h ^ b) * 0x100000001b3UL;
        ulong m = (h >> 23) ^ (h << 17) ^ h;
        ulong a = m ^ K9;
        ulong b2 = (m ^ K10) + (a << 13);
        ulong c = (m ^ K11) - (a >> 7);
        ulong d = K12 - (b2 ^ c);
        ulong g = (d + c) ^ 0xf0f0f0f0f0f0f0fUL;
        ulong hv = g ^ b2;
        ulong fv = d ^ c;

        key = new byte[32];
        iv = new byte[16];
        WriteLe(key, 0, b2); WriteLe(key, 8, c); WriteLe(key, 16, d); WriteLe(key, 24, g);
        WriteLe(iv, 0, hv); WriteLe(iv, 8, fv);
    }

    private static void WriteLe(byte[] buf, int off, ulong v)
    {
        for (int i = 0; i < 8; i++) buf[off + i] = (byte)(v >> (8 * i));
    }
}
