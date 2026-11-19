using NopeView.App.Native;

namespace NopeView.App;

/// <summary>
/// User-facing error and status text. Keeping it here means wording changes,
/// and later localization, do not require hunting through the window code.
/// </summary>
internal static class Strings
{
    internal const string OpenTitle = "Could not open image";
    internal const string CopyTitle = "Could not copy image";
    internal const string SaveTitle = "Could not save image";
    internal const string AnimationTitle = "Could not play animation";

    internal const string RouterUnavailable = "Codec router is unavailable.";
    internal const string CopyDecodeFailed = "The current image could not be decoded for copying.";

    internal static string OpenFailed(string reason) => "Failed to open image: " + reason;

    internal static string CopyFailed(string reason) => "Failed to copy image: " + reason;

    internal static string SaveFailed(string reason) => "Failed to save image: " + reason;

    internal static string DescribeDecodeStatus(int status) => status switch
    {
        RouterApi.ErrUnsupportedFormat => "Unsupported image format.",
        RouterApi.ErrDecodeFailed => "The image is damaged or cannot be decoded.",
        RouterApi.ErrOutOfMemory => "Not enough memory to decode this image.",
        RouterApi.ErrIo => "The file could not be read.",
        RouterApi.ErrAbiMismatch => "Codec ABI mismatch.",
        _ => $"Failed to open image (status {status})."
    };

    internal static string DescribeEncodeStatus(int status) => status switch
    {
        RouterApi.ErrUnsupportedFormat => "The target format cannot be encoded.",
        RouterApi.ErrEncodeFailed => "The image could not be encoded.",
        RouterApi.ErrOutOfMemory => "Not enough memory to encode this image.",
        RouterApi.ErrIo => "The file could not be written.",
        _ => $"The image could not be saved (status {status})."
    };
}
