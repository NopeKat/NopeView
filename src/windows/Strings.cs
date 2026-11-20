using NopeView.App.Native;

namespace NopeView.App;

/// <summary>
/// User-facing error and status text. Every string is looked up by key so the
/// wording lives in the resource files and follows the app language.
/// </summary>
internal static class Strings
{
    internal static string OpenTitle => Localization.Get("Error.OpenTitle");
    internal static string CopyTitle => Localization.Get("Error.CopyTitle");
    internal static string SaveTitle => Localization.Get("Error.SaveTitle");
    internal static string AnimationTitle => Localization.Get("Error.AnimationTitle");

    internal static string RouterUnavailable => Localization.Get("Error.RouterUnavailable");
    internal static string CopyDecodeFailed => Localization.Get("Error.CopyDecodeFailed");

    internal static string OpenFailed(string reason) =>
        Localization.Format("Error.OpenFailed", reason);

    internal static string CopyFailed(string reason) =>
        Localization.Format("Error.CopyFailed", reason);

    internal static string SaveFailed(string reason) =>
        Localization.Format("Error.SaveFailed", reason);

    internal static string DescribeDecodeStatus(int status) => status switch
    {
        RouterApi.ErrUnsupportedFormat => Localization.Get("Error.DecodeUnsupported"),
        RouterApi.ErrDecodeFailed => Localization.Get("Error.DecodeFailed"),
        RouterApi.ErrOutOfMemory => Localization.Get("Error.DecodeOutOfMemory"),
        RouterApi.ErrIo => Localization.Get("Error.DecodeIo"),
        RouterApi.ErrAbiMismatch => Localization.Get("Error.DecodeAbi"),
        _ => Localization.Format("Error.DecodeStatus", status)
    };

    internal static string DescribeEncodeStatus(int status) => status switch
    {
        RouterApi.ErrUnsupportedFormat => Localization.Get("Error.EncodeUnsupported"),
        RouterApi.ErrEncodeFailed => Localization.Get("Error.EncodeFailed"),
        RouterApi.ErrOutOfMemory => Localization.Get("Error.EncodeOutOfMemory"),
        RouterApi.ErrIo => Localization.Get("Error.EncodeIo"),
        _ => Localization.Format("Error.EncodeStatus", status)
    };
}
