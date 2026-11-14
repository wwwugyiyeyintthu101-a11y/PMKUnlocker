using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace PMKUnlocker
{
    // Samsung Download (Odin) SoftBrick Reset — Odin/LOKE bulk USB (Thor Odin.cs sequence).
    // Handshake ODIN→LOKE → BeginSession 0x64/0x00 → ResetFlashCount 0x64/0x01 → EndSession 0x67/0x00
    // → optional RebootToOdin 0x67/0x02 (error flag clear — firmware reflash မလို).
    public static class SamsungSoftBrick
    {
        public const int SamsungVid = 0x04E8;

        // Download-mode PIDs (classic 685D + common alts)
        public static readonly int[] DownloadPids =
        {
            0x685D, 0x6866, 0x6877, 0x685C, 0x685E, 0x6860, 0x6A1A, 0x6A1B
        };

        public static byte[] BuildPacket(int region, int sub, int? dword8 = null)
        {
            var buf = new byte[1024];
            WriteInt(buf, region, 0);
            WriteInt(buf, sub, 4);
            if (dword8.HasValue) WriteInt(buf, dword8.Value, 8);
            return buf;
        }

        public static byte[] BeginSessionPacket() => BuildPacket(0x64, 0x00, int.MaxValue);
        public static byte[] ResetFlashCountPacket() => BuildPacket(0x64, 0x01);
        public static byte[] EndSessionPacket() => BuildPacket(0x67, 0x00);
        public static byte[] RebootToOdinPacket() => BuildPacket(0x67, 0x02);
        public static byte[] SendFilePartSizePacket(int size) => BuildPacket(0x64, 0x05, size);

        // Thor OdinFailCheck: response[0]==0xFF → error code LE int at offset 4.
        public static void FailCheck(byte[] buf, string id)
        {
            if (buf == null || buf.Length < 1) throw new InvalidDataException(id + ": empty response");
            if (buf[0] != 0xFF) return;
            int code = buf.Length >= 8 ? ReadInt(buf, 4) : -1;
            throw new InvalidDataException($"{id} failed (code 0x{code:X4})");
        }

        public static string? FindDownloadModeDevice(out int vid, out int pid)
        {
            vid = 0;
            pid = 0;
            try
            {
                foreach (LibUsbDotNet.Main.UsbRegistry reg in UsbDevice.AllDevices)
                {
                    if (reg.Vid != SamsungVid) continue;
                    if (!DownloadPids.Contains(reg.Pid)) continue;
                    vid = reg.Vid;
                    pid = reg.Pid;
                    string nm = reg.Name ?? "";
                    return $"04E8:{reg.Pid:X4} " + (string.IsNullOrWhiteSpace(nm) ? "Download mode" : nm);
                }
            }
            catch { /* driver/libusb missing → null */ }
            return null;
        }

        public static bool IsDownloadModePresent() => FindDownloadModeDevice(out _, out _) != null;

        // Full SoftBrick sequence. log may be null. Returns true if flash-count reset succeeded.
        public static bool ResetFlashCount(Action<string>? log, bool rebootToOdin, out string error)
        {
            error = "";
            void Say(string m) => log?.Invoke(m);

            string? found = FindDownloadModeDevice(out int vid, out int pid);
            if (found == null)
            {
                error = "Download mode USB device not found (VID_04E8)";
                return false;
            }
            Say("  • Device        : " + found);

            UsbDevice? dev = null;
            UsbEndpointWriter? writer = null;
            UsbEndpointReader? reader = null;
            int claimedIface = -1;
            try
            {
                dev = UsbDevice.OpenUsbDevice(new UsbDeviceFinder(vid, pid));
                if (dev == null)
                {
                    error = "Open failed — device busy (heimdall/Odin) or access denied";
                    return false;
                }

                IUsbDevice? whole = dev as IUsbDevice;
                if (whole != null)
                {
                    whole.SetConfiguration(1);
                    // Claim interface that exposes bulk IN+OUT (often 0 or CDC-Data 1)
                    byte? inEp = null, outEp = null;
                    int ifaceNum = 0;
                    foreach (var cfg in dev.Configs)
                    {
                        foreach (var iface in cfg.InterfaceInfoList)
                        {
                            byte? iEp = null, oEp = null;
                            foreach (var ep in iface.EndpointInfoList)
                            {
                                byte addr = ep.Descriptor.EndpointID;
                                byte transfer = (byte)(ep.Descriptor.Attributes & 0x03);
                                if (transfer != 2) continue; // bulk only
                                if ((addr & 0x80) != 0) iEp = addr;
                                else oEp = addr;
                            }
                            if (iEp.HasValue && oEp.HasValue)
                            {
                                inEp = iEp;
                                outEp = oEp;
                                ifaceNum = iface.Descriptor.InterfaceID;
                                break;
                            }
                        }
                        if (inEp.HasValue) break;
                    }

                    if (!inEp.HasValue || !outEp.HasValue)
                    {
                        // Fallback — Heimdall/Thor often use 0x81 IN / 0x02 OUT on iface 1
                        inEp = 0x81;
                        outEp = 0x02;
                        ifaceNum = 1;
                    }

                    if (!whole.ClaimInterface(ifaceNum))
                    {
                        // try iface 0 as fallback
                        if (ifaceNum != 0 && whole.ClaimInterface(0)) { ifaceNum = 0; }
                        else
                        {
                            error = "ClaimInterface failed — close heimdall/Odin, re-plug cable";
                            return false;
                        }
                    }
                    claimedIface = ifaceNum;

                    writer = dev.OpenEndpointWriter((WriteEndpointID)(outEp.Value & 0x7F), EndpointType.Bulk);
                    reader = dev.OpenEndpointReader((ReadEndpointID)inEp.Value, 512, EndpointType.Bulk);
                    Say($"  • Bulk EP       : OUT 0x{outEp.Value:X2} / IN 0x{inEp.Value:X2} (iface {ifaceNum})");
                }
                else
                {
                    // WinUSB interface object — open first bulk pair by probing common EPs
                    writer = dev.OpenEndpointWriter(WriteEndpointID.Ep02, EndpointType.Bulk);
                    reader = dev.OpenEndpointReader(ReadEndpointID.Ep01, 512, EndpointType.Bulk);
                    Say("  • Bulk EP       : OUT 0x02 / IN 0x81 (WinUSB default)");
                }

                const int timeout = 5000;

                // 1) Handshake
                Say("[*] Handshake ODIN → LOKE ...");
                var hs = Encoding.ASCII.GetBytes("ODIN");
                int written;
                var we = writer.Write(hs, 0, hs.Length, timeout, out written);
                if (we != ErrorCode.None || written != hs.Length)
                {
                    error = "Handshake write failed: " + we;
                    return false;
                }
                var hsBuf = new byte[512];
                var re = reader.Read(hsBuf, 0, hsBuf.Length, timeout, out int hread);
                if (re != ErrorCode.None || hread < 4)
                {
                    error = "Handshake read failed: " + re + " (not in Download mode?)";
                    return false;
                }
                string loke = Encoding.ASCII.GetString(hsBuf, 0, 4);
                if (loke != "LOKE")
                {
                    error = "Expected LOKE, got '" + loke + "'";
                    return false;
                }
                Say("  • LOKE          : OK");

                // 2) BeginSession
                Say("[*] Begin session (0x64/0x00) ...");
                if (!WriteRead(writer, reader, BeginSessionPacket(), out byte[] sess, out error, "BeginSession"))
                    return false;
                FailCheck(sess, "BeginSession");
                short protoVer = sess.Length >= 8 ? (short)((sess[6] | (sess[7] << 8))) : (short)0;
                int blVer = sess.Length >= 8 ? ReadInt(sess, 4) : 0;
                Say($"  • Session       : proto v{protoVer} (BL 0x{blVer:X8})");

                // 3) File part size — only proto v2+ (Thor BeginSession)
                if (protoVer > 1)
                {
                    int partSize = 1048576;
                    Say("[*] Set file part size (0x64/0x05) ...");
                    if (!WriteRead(writer, reader, SendFilePartSizePacket(partSize), out byte[] fps, out error, "SendFilePartSize"))
                        return false;
                    FailCheck(fps, "SendFilePartSize");
                }

                // 4) Reset flash count — softbrick fix core
                Say("[*] Reset flash count (0x64/0x01) ...");
                if (!WriteRead(writer, reader, ResetFlashCountPacket(), out byte[] rst, out error, "ResetFlashCount"))
                    return false;
                FailCheck(rst, "ResetFlashCount");
                Say("[OK] Flash count reset — error flag cleared.");

                // 5) End session
                Say("[*] End session (0x67/0x00) ...");
                if (WriteRead(writer, reader, EndSessionPacket(), out byte[] end, out string endErr, "EndSession"))
                {
                    try { FailCheck(end, "EndSession"); Say("  • Session       : closed"); }
                    catch (Exception ex) { Say("[!] EndSession: " + ex.Message); }
                }
                else Say("[!] EndSession: " + endErr);

                // 6) Reboot back to normal Download mode
                if (rebootToOdin)
                {
                    Say("[*] Reboot to Odin/Download (0x67/0x02) ...");
                    if (WriteRead(writer, reader, RebootToOdinPacket(), out byte[] rb, out string rbErr, "RebootToOdin"))
                    {
                        try
                        {
                            FailCheck(rb, "RebootToOdin");
                            Say("[OK] Device rebooting into Download mode.");
                        }
                        catch (Exception ex)
                        {
                            Say("[i] RebootToOdin: " + ex.Message + " — power-cycle into Download mode manually if needed.");
                        }
                    }
                    else Say("[i] RebootToOdin: " + rbErr);
                }

                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (claimedIface >= 0 && dev is IUsbDevice w) w.ReleaseInterface(claimedIface); } catch { }
                try { writer?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
                try { if (dev != null && dev.IsOpen) dev.Close(); } catch { }
            }
        }

        private static bool WriteRead(
            UsbEndpointWriter writer,
            UsbEndpointReader reader,
            byte[] payload,
            out byte[] response,
            out string error,
            string id,
            int timeout = 5000)
        {
            response = Array.Empty<byte>();
            error = "";
            var we = writer.Write(payload, 0, payload.Length, timeout, out int written);
            if (we != ErrorCode.None || written != payload.Length)
            {
                error = id + " write failed: " + we;
                return false;
            }
            var buf = new byte[512];
            var re = reader.Read(buf, 0, buf.Length, timeout, out int read);
            if (re != ErrorCode.None || read < 8)
            {
                error = id + " read failed: " + re + " (got " + read + " bytes)";
                return false;
            }
            response = buf;
            return true;
        }

        private static void WriteInt(byte[] buf, int value, int offset)
        {
            var b = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            buf[offset] = b[0];
            buf[offset + 1] = b[1];
            buf[offset + 2] = b[2];
            buf[offset + 3] = b[3];
        }

        private static int ReadInt(byte[] buf, int offset)
        {
            if (offset + 4 > buf.Length) return -1;
            var b = new[] { buf[offset], buf[offset + 1], buf[offset + 2], buf[offset + 3] };
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }
    }
}
