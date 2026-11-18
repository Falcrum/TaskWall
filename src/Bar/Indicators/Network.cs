using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskWall;

/// <summary>
/// The computer's connection for the bar: Wi-Fi (the network's name and the signal), cable or none, the address, the link's
/// speed, and whether the internet answers (Windows' own Network List Manager, the same that puts the globe on the taskbar's icon).
/// Changes arrive from Windows (NetworkChange); only the Wi-Fi signal is read again now and then while on Wi-Fi.
/// </summary>
static class Network
{
    public enum Kind { None, Wifi, Cable, Other }

    public sealed record State(Kind Kind, string Name, int Signal, bool Internet, string Address, long SpeedBps, string Adapter);

    /// <summary>The state now (a few milliseconds: call it when told something changed).</summary>
    public static State Read()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Nic: n, Props: n.GetIPProperties()))
                .Where(x => x.Props.GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
                .OrderBy(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0) // a cable wins, like Windows' choice
                .FirstOrDefault();
            bool internet = Internet();
            if (up.Nic == null) return new State(Kind.None, "", 0, internet, "", 0, "");
            var nic = up.Nic;
            var address = up.Props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                var (ssid, signal) = Wifi();
                return new State(Kind.Wifi, ssid.Length > 0 ? ssid : nic.Name, signal, internet, address, nic.Speed, nic.Description);
            }
            var kind = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx ? Kind.Cable : Kind.Other;
            return new State(kind, nic.Name, 0, internet, address, nic.Speed, nic.Description);
        }
        catch (Exception ex) { Log.Error("network", ex); return new State(Kind.None, "", 0, false, "", 0, ""); }
    }

    /// <summary>Whether Windows sees the internet (not just a local network): INetworkListManager.IsConnectedToInternet.</summary>
    static bool Internet()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type == null) return NetworkInterface.GetIsNetworkAvailable();
            dynamic nlm = Activator.CreateInstance(type)!;
            try { return (bool)nlm.IsConnectedToInternet; }
            finally { Marshal.FinalReleaseComObject(nlm); }
        }
        catch { return NetworkInterface.GetIsNetworkAvailable(); }
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    /// <summary>
    /// The connected Wi-Fi network's name and signal quality (0–100) from the WLAN service: WLAN_CONNECTION_ATTRIBUTES, read by
    /// offset (the association attributes begin after the state, the mode and the 256-character profile name).
    /// </summary>
    static (string Ssid, int Signal) Wifi()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return ("", 0);
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return ("", 0);
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID (16) + 256 wide chars (512) + state (4)
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    int state = Marshal.ReadInt32(item + 528);
                    if (state != 1 /* wlan_interface_state_connected */) continue;
                    if (WlanQueryInterface(h, ref guid, 7 /* wlan_intf_opcode_current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                    try
                    {
                        const int assoc = 520;
                        int len = Math.Clamp(Marshal.ReadInt32(data + assoc), 0, 32);
                        var bytes = new byte[len];
                        Marshal.Copy(data + assoc + 4, bytes, 0, len);
                        int signal = Marshal.ReadInt32(data + assoc + 56);
                        return (Encoding.UTF8.GetString(bytes), Math.Clamp(signal, 0, 100));
                    }
                    finally { WlanFreeMemory(data); }
                }
                return ("", 0);
            }
            finally { WlanFreeMemory(list); }
        }
        catch { return ("", 0); }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }
}
