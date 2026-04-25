using System.Collections;
using System.Text;

namespace Podish.Pulse.Protocol;

/// <summary>
/// A list of key-value pairs that associate arbitrary properties with an object.
/// Keys are null-terminated strings and values are arbitrary binary blobs,
/// although by convention both are usually null-terminated ASCII strings.
/// </summary>
public sealed class Props : IEnumerable<KeyValuePair<string, byte[]>>
{
    private readonly Dictionary<string, byte[]> _properties;

    /// <summary>
    /// Creates a new, empty property list.
    /// </summary>
    public Props()
    {
        _properties = new Dictionary<string, byte[]>();
    }

    /// <summary>
    /// Sets a well-known property in the map.
    /// If the property already has a value, it will be overwritten with the new one.
    /// </summary>
    /// <param name="prop">The property to set.</param>
    /// <param name="value">The value.</param>
    public void Set(Prop prop, string value)
    {
        SetBytes(prop.ToCString(), Encoding.UTF8.GetBytes(value + "\0"));
    }

    /// <summary>
    /// Sets a property in the map.
    /// If the property already has a value, it will be overwritten with the new one.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value as a byte array.</param>
    public void SetBytes(string key, byte[] value)
    {
        _properties[key] = value;
    }

    /// <summary>
    /// Sets a property in the map.
    /// If the property already has a value, it will be overwritten with the new one.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value as a string.</param>
    public void SetString(string key, string value)
    {
        _properties[key] = Encoding.UTF8.GetBytes(value + "\0");
    }

    /// <summary>
    /// Gets the value of a well-known property.
    /// </summary>
    /// <param name="prop">The property.</param>
    /// <returns>The value as a byte array, or null if not found.</returns>
    public byte[]? Get(Prop prop)
    {
        return GetBytes(prop.ToCString());
    }

    /// <summary>
    /// Gets a property from the map.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value as a byte array, or null if not found.</returns>
    public byte[]? GetBytes(string key)
    {
        return _properties.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Gets a property from the map as a string.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value as a string, or null if not found.</returns>
    public string? GetString(string key)
    {
        if (!_properties.TryGetValue(key, out var value))
            return null;
        // Trim null terminator if present
        if (value.Length > 0 && value[value.Length - 1] == 0)
            value = value[..^1];
        return Encoding.UTF8.GetString(value);
    }

    /// <summary>
    /// Gets a property from the map as a string.
    /// </summary>
    /// <param name="prop">The property.</param>
    /// <returns>The value as a string, or null if not found.</returns>
    public string? GetString(Prop prop)
    {
        return GetString(prop.ToCString());
    }

    /// <summary>
    /// Checks if a property exists in the map.
    /// </summary>
    /// <param name="prop">The property.</param>
    /// <returns>True if the property exists.</returns>
    public bool Contains(Prop prop)
    {
        return _properties.ContainsKey(prop.ToCString());
    }

    /// <summary>
    /// Checks if a property exists in the map.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True if the property exists.</returns>
    public bool Contains(string key)
    {
        return _properties.ContainsKey(key);
    }

    /// <summary>
    /// Removes a property from the map.
    /// </summary>
    /// <param name="prop">The property.</param>
    /// <returns>True if the property was removed.</returns>
    public bool Remove(Prop prop)
    {
        return _properties.Remove(prop.ToCString());
    }

    /// <summary>
    /// Removes a property from the map.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>True if the property was removed.</returns>
    public bool Remove(string key)
    {
        return _properties.Remove(key);
    }

    /// <summary>
    /// Gets the number of properties in the map.
    /// </summary>
    public int Count => _properties.Count;

    /// <summary>
    /// Clears all properties from the map.
    /// </summary>
    public void Clear()
    {
        _properties.Clear();
    }

    public Props Clone()
    {
        var clone = new Props();
        foreach (var kvp in _properties)
            clone._properties[kvp.Key] = kvp.Value.ToArray();
        return clone;
    }

    public void Update(PropsUpdateMode mode, Props other)
    {
        switch (mode)
        {
            case PropsUpdateMode.Set:
                _properties.Clear();
                foreach (var kvp in other._properties)
                    _properties[kvp.Key] = kvp.Value.ToArray();
                break;
            case PropsUpdateMode.Merge:
                foreach (var kvp in other._properties)
                {
                    if (!_properties.ContainsKey(kvp.Key))
                        _properties[kvp.Key] = kvp.Value.ToArray();
                }
                break;
            case PropsUpdateMode.Replace:
                foreach (var kvp in other._properties)
                    _properties[kvp.Key] = kvp.Value.ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    public int RemoveKeys(IEnumerable<string> keys)
    {
        int removed = 0;
        foreach (string key in keys)
        {
            if (_properties.Remove(key))
                removed++;
        }

        return removed;
    }

    public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator()
    {
        return _properties.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _properties.GetEnumerator();
    }
}

/// <summary>
/// Well-known property list keys.
/// </summary>
public enum Prop
{
    // Media properties
    /// <summary>For streams: localized media name, formatted as UTF-8. E.g. "Guns'N'Roses: Civil War".</summary>
    MediaName,
    /// <summary>For streams: localized media title if applicable, formatted as UTF-8. E.g. "Civil War"</summary>
    MediaTitle,
    /// <summary>For streams: localized media artist if applicable, formatted as UTF-8. E.g. "Guns'N'Roses"</summary>
    MediaArtist,
    /// <summary>For streams: localized media copyright string if applicable, formatted as UTF-8.</summary>
    MediaCopyright,
    /// <summary>For streams: localized media generator software string if applicable, formatted as UTF-8.</summary>
    MediaSoftware,
    /// <summary>For streams: media language if applicable, in standard POSIX format. E.g. "de_DE"</summary>
    MediaLanguage,
    /// <summary>For streams: source filename if applicable, in URI format or local path.</summary>
    MediaFilename,
    /// <summary>For streams: icon for the media. A binary blob containing PNG image data</summary>
    MediaIcon,
    /// <summary>For streams: an XDG icon name for the media. E.g. "audio-x-mp3"</summary>
    MediaIconName,
    /// <summary>For streams: logic role of this media. One of the strings "video", "music", "game", "event", "phone", "animation", "production", "a11y", "test"</summary>
    MediaRole,
    /// <summary>For streams: the name of a filter that is desired.</summary>
    FilterWant,
    /// <summary>For streams: the name of a filter that is desired (forces application).</summary>
    FilterApply,
    /// <summary>For streams: the name of a filter that should specifically be suppressed.</summary>
    FilterSuppress,
    /// <summary>For event sound streams: XDG event sound name.</summary>
    EventId,
    /// <summary>For event sound streams: localized human readable one-line description of the event.</summary>
    EventDescription,
    /// <summary>For event sound streams: absolute horizontal mouse position on the screen.</summary>
    EventMouseX,
    /// <summary>For event sound streams: absolute vertical mouse position on the screen.</summary>
    EventMouseY,
    /// <summary>For event sound streams: relative horizontal mouse position on the screen.</summary>
    EventMouseHPos,
    /// <summary>For event sound streams: relative vertical mouse position on the screen.</summary>
    EventMouseVPos,
    /// <summary>For event sound streams: mouse button that triggered the event.</summary>
    EventMouseButton,
    /// <summary>For streams that belong to a window on the screen: localized window title.</summary>
    WindowName,
    /// <summary>For streams that belong to a window on the screen: a textual id for identifying a window logically.</summary>
    WindowId,
    /// <summary>For streams that belong to a window on the screen: window icon.</summary>
    WindowIcon,
    /// <summary>For streams that belong to a window on the screen: an XDG icon name for the window.</summary>
    WindowIconName,
    /// <summary>For streams that belong to a window on the screen: absolute horizontal window position.</summary>
    WindowX,
    /// <summary>For streams that belong to a window on the screen: absolute vertical window position.</summary>
    WindowY,
    /// <summary>For streams that belong to a window on the screen: window width.</summary>
    WindowWidth,
    /// <summary>For streams that belong to a window on the screen: window height.</summary>
    WindowHeight,
    /// <summary>For streams that belong to a window on the screen: relative horizontal window center position.</summary>
    WindowHPos,
    /// <summary>For streams that belong to a window on the screen: relative vertical window center position.</summary>
    WindowVPos,
    /// <summary>For streams that belong to a window on the screen: desktop list.</summary>
    WindowDesktop,
    /// <summary>For streams that belong to an X11 window: the X11 display string.</summary>
    WindowX11Display,
    /// <summary>For streams that belong to an X11 window: the X11 screen.</summary>
    WindowX11Screen,
    /// <summary>For streams that belong to an X11 window: the X11 monitor.</summary>
    WindowX11Monitor,
    /// <summary>For streams that belong to an X11 window: the window XID.</summary>
    WindowX11Xid,
    // Application properties
    /// <summary>For clients/streams: localized human readable application name.</summary>
    ApplicationName,
    /// <summary>For clients/streams: a textual id for identifying an application logically.</summary>
    ApplicationId,
    /// <summary>For clients/streams: a version string.</summary>
    ApplicationVersion,
    /// <summary>For clients/streams: application icon.</summary>
    ApplicationIcon,
    /// <summary>For clients/streams: an XDG icon name for the application.</summary>
    ApplicationIconName,
    /// <summary>For clients/streams: application language.</summary>
    ApplicationLanguage,
    /// <summary>For clients/streams on UNIX: application process PID.</summary>
    ApplicationProcessId,
    /// <summary>For clients/streams: application process name.</summary>
    ApplicationProcessBinary,
    /// <summary>For clients/streams: application user name.</summary>
    ApplicationProcessUser,
    /// <summary>For clients/streams: host name the application runs on.</summary>
    ApplicationProcessHost,
    /// <summary>For clients/streams: the D-Bus host id.</summary>
    ApplicationProcessMachineId,
    /// <summary>For clients/streams: login session id.</summary>
    ApplicationProcessSessionId,
    // Device properties
    /// <summary>For devices: device string.</summary>
    DeviceString,
    /// <summary>For devices: API this device is accessed with.</summary>
    DeviceApi,
    /// <summary>For devices: localized human readable device description.</summary>
    DeviceDescription,
    /// <summary>For devices: bus path to the device.</summary>
    DeviceBusPath,
    /// <summary>For devices: serial number.</summary>
    DeviceSerial,
    /// <summary>For devices: vendor ID.</summary>
    DeviceVendorId,
    /// <summary>For devices: vendor name.</summary>
    DeviceVendorName,
    /// <summary>For devices: product ID.</summary>
    DeviceProductId,
    /// <summary>For devices: product name.</summary>
    DeviceProductName,
    /// <summary>For devices: device class.</summary>
    DeviceClass,
    /// <summary>For devices: form factor.</summary>
    DeviceFormFactor,
    /// <summary>For devices: bus of the device.</summary>
    DeviceBus,
    /// <summary>For devices: device icon.</summary>
    DeviceIcon,
    /// <summary>For devices: an XDG icon name for the device.</summary>
    DeviceIconName,
    /// <summary>For devices: access mode.</summary>
    DeviceAccessMode,
    /// <summary>For filter devices: master device id.</summary>
    DeviceMasterDevice,
    /// <summary>For devices: buffer size in bytes.</summary>
    DeviceBufferingBufferSize,
    /// <summary>For devices: fragment size in bytes.</summary>
    DeviceBufferingFragmentSize,
    /// <summary>For devices: profile identifier.</summary>
    DeviceProfileName,
    /// <summary>For devices: intended use.</summary>
    DeviceIntendedRoles,
    /// <summary>For devices: profile description.</summary>
    DeviceProfileDescription,
    // Module properties
    /// <summary>For modules: the author's name.</summary>
    ModuleAuthor,
    /// <summary>For modules: module description.</summary>
    ModuleDescription,
    /// <summary>For modules: module usage.</summary>
    ModuleUsage,
    /// <summary>For modules: module version.</summary>
    ModuleVersion,
    // Format properties
    /// <summary>For PCM formats: the sample format.</summary>
    FormatSampleFormat,
    /// <summary>For all formats: the sample rate.</summary>
    FormatRate,
    /// <summary>For all formats: the number of channels.</summary>
    FormatChannels,
    /// <summary>For PCM formats: the channel map.</summary>
    FormatChannelMap,
}

/// <summary>
/// Extension methods for Prop enum.
/// </summary>
public static class PropExtensions
{
    /// <summary>
    /// Returns the property name to use in a property list.
    /// </summary>
    /// <param name="prop">The property.</param>
    /// <returns>The property name as a string.</returns>
    public static string ToCString(this Prop prop)
    {
        return prop switch
        {
            Prop.MediaName => "media.name",
            Prop.MediaTitle => "media.title",
            Prop.MediaArtist => "media.artist",
            Prop.MediaCopyright => "media.copyright",
            Prop.MediaSoftware => "media.software",
            Prop.MediaLanguage => "media.language",
            Prop.MediaFilename => "media.filename",
            Prop.MediaIcon => "media.icon",
            Prop.MediaIconName => "media.icon_name",
            Prop.MediaRole => "media.role",
            Prop.FilterWant => "filter.want",
            Prop.FilterApply => "filter.apply",
            Prop.FilterSuppress => "filter.suppress",
            Prop.EventId => "event.id",
            Prop.EventDescription => "event.description",
            Prop.EventMouseX => "event.mouse.x",
            Prop.EventMouseY => "event.mouse.y",
            Prop.EventMouseHPos => "event.mouse.hpos",
            Prop.EventMouseVPos => "event.mouse.vpos",
            Prop.EventMouseButton => "event.mouse.button",
            Prop.WindowName => "window.name",
            Prop.WindowId => "window.id",
            Prop.WindowIcon => "window.icon",
            Prop.WindowIconName => "window.icon_name",
            Prop.WindowX => "window.x",
            Prop.WindowY => "window.y",
            Prop.WindowWidth => "window.width",
            Prop.WindowHeight => "window.height",
            Prop.WindowHPos => "window.hpos",
            Prop.WindowVPos => "window.vpos",
            Prop.WindowDesktop => "window.desktop",
            Prop.WindowX11Display => "window.x11.display",
            Prop.WindowX11Screen => "window.x11.screen",
            Prop.WindowX11Monitor => "window.x11.monitor",
            Prop.WindowX11Xid => "window.x11.xid",
            Prop.ApplicationName => "application.name",
            Prop.ApplicationId => "application.id",
            Prop.ApplicationVersion => "application.version",
            Prop.ApplicationIcon => "application.icon",
            Prop.ApplicationIconName => "application.icon_name",
            Prop.ApplicationLanguage => "application.language",
            Prop.ApplicationProcessId => "application.process.id",
            Prop.ApplicationProcessBinary => "application.process.binary",
            Prop.ApplicationProcessUser => "application.process.user",
            Prop.ApplicationProcessHost => "application.process.host",
            Prop.ApplicationProcessMachineId => "application.process.machine_id",
            Prop.ApplicationProcessSessionId => "application.process.session_id",
            Prop.DeviceString => "device.string",
            Prop.DeviceApi => "device.api",
            Prop.DeviceDescription => "device.description",
            Prop.DeviceBusPath => "device.bus_path",
            Prop.DeviceSerial => "device.serial",
            Prop.DeviceVendorId => "device.vendor.id",
            Prop.DeviceVendorName => "device.vendor.name",
            Prop.DeviceProductId => "device.product.id",
            Prop.DeviceProductName => "device.product.name",
            Prop.DeviceClass => "device.class",
            Prop.DeviceFormFactor => "device.form_factor",
            Prop.DeviceBus => "device.bus",
            Prop.DeviceIcon => "device.icon",
            Prop.DeviceIconName => "device.icon_name",
            Prop.DeviceAccessMode => "device.access_mode",
            Prop.DeviceMasterDevice => "device.master_device",
            Prop.DeviceBufferingBufferSize => "device.buffering.buffer_size",
            Prop.DeviceBufferingFragmentSize => "device.buffering.fragment_size",
            Prop.DeviceProfileName => "device.profile.name",
            Prop.DeviceIntendedRoles => "device.intended_roles",
            Prop.DeviceProfileDescription => "device.profile.description",
            Prop.ModuleAuthor => "module.author",
            Prop.ModuleDescription => "module.description",
            Prop.ModuleUsage => "module.usage",
            Prop.ModuleVersion => "module.version",
            Prop.FormatSampleFormat => "format.sample_format",
            Prop.FormatRate => "format.rate",
            Prop.FormatChannels => "format.channels",
            Prop.FormatChannelMap => "format.channel_map",
            _ => throw new ArgumentOutOfRangeException(nameof(prop), prop, null),
        };
    }
}

/// <summary>
/// Extension methods for TagStructReader and TagStructWriter to read/write Props.
/// </summary>
public static class PropsExtensions
{
    /// <summary>
    /// Reads a Props from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The property list.</returns>
    public static Props ReadProps(this TagStructReader reader)
    {
        reader.ExpectTag(Tag.PropList);
        
        var props = new Props();
        while (true)
        {
            string? key = reader.ReadString();
            if (key == null)
                break; // End of property list
            
            if (key.Length == 0)
                throw new InvalidProtocolMessageException("Proplist key is empty");
            
            uint len = reader.ReadU32();
            if (len > Constants.MaxPropSize)
                throw new InvalidProtocolMessageException($"Proplist value size {len} exceeds hard limit of {Constants.MaxPropSize} bytes");

            byte[] value = reader.ReadArbitrary();
            if (len != value.Length)
                throw new InvalidProtocolMessageException($"Proplist expected value size {len} does not match actual size {value.Length}");
            props.SetBytes(key, value);
        }
        
        return props;
    }

    /// <summary>
    /// Writes a Props to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="props">The property list.</param>
    public static void WriteProps(this TagStructWriter writer, Props props)
    {
        writer.WriteTag(Tag.PropList);
        
        foreach (var kvp in props)
        {
            writer.WriteString(kvp.Key);
            writer.WriteU32((uint)kvp.Value.Length);
            writer.WriteArbitrary(kvp.Value);
        }
        
        writer.WriteNullString(); // End of property list
    }
}

/// <summary>
/// The mode of a Props update operation.
/// </summary>
public enum PropsUpdateMode : uint
{
    /// <summary>
    /// Replace the entire property list with the new one.
    /// </summary>
    Set = 0,
    /// <summary>
    /// Merge the new properties into the existing list.
    /// </summary>
    Merge = 1,
    /// <summary>
    /// Replace existing matching properties with the new values.
    /// </summary>
    Replace = 2,
}
