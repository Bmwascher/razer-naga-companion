namespace NagaBatteryTray.Hid;

/// <summary>One open Razer control collection: the 90+1 byte feature-report endpoint, and the ONLY
/// place Win32 appears in the connection path. Everything above this interface — preference order,
/// reselect rules, rollback, disposal, connection identity — is ordinary testable code
/// (spec 2026-08-19 §6, product-owner decision 2026-08-19).</summary>
internal interface IControlCollection : IDisposable
{
    /// <summary>USB product id this collection was opened from; identifies model and link.</summary>
    int Pid { get; }

    /// <summary>False once a feature call has failed or the collection has been disposed. Callers
    /// check it before every exchange, exactly as the old code checked its handle for null: a failed
    /// HID call means the collection is finished, and probing it further is wasted Win32 calls on a
    /// dead handle that can also make an intermittent failure look like a working candidate.</summary>
    bool IsOpen { get; }

    /// <summary>HidD_SetFeature. False = the call failed and this collection is finished.</summary>
    bool SetFeature(byte[] buffer);

    /// <summary>HidD_GetFeature. False = the call failed and this collection is finished.</summary>
    bool GetFeature(byte[] buffer);
}
