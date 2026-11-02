using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace DynamicIsland;

sealed class BluetoothService
{
    public readonly record struct State(bool Present, bool On, string Device);

    static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    DateTime _checkedAt;
    bool _reading;

    public event Action? Changed;

    public State Current { get; private set; }

    public async Task RefreshAsync(bool force = false)
    {
        if (_reading || (!force && DateTime.UtcNow - _checkedAt < Pause)) return;
        _reading = true;
        try
        {
            State next = await Task.Run(Read);
            _checkedAt = DateTime.UtcNow;
            if (next == Current) return;
            Current = next;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _reading = false;
        }
    }

    public async Task ToggleAsync()
    {
        try
        {
            Radio? radio = await FindRadio();
            if (radio == null) return;
            await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
            await RefreshAsync(true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    static State Read()
    {
        try
        {
            Radio? radio = FindRadio().GetAwaiter().GetResult();
            if (radio == null) return default;
            bool on = radio.State == RadioState.On;
            return new State(true, on, on ? ConnectedName() : "");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return default;
        }
    }

    static async Task<Radio?> FindRadio()
    {
        foreach (Radio radio in await Radio.GetRadiosAsync())
            if (radio.Kind == RadioKind.Bluetooth) return radio;
        return null;
    }

    static string ConnectedName()
    {
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            DeviceInformationCollection devices = DeviceInformation.FindAllAsync(selector).AsTask().GetAwaiter().GetResult();
            return devices.Count > 0 ? devices[0].Name : "";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return "";
        }
    }
}
