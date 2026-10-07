namespace Cairn.Formats;

/// <summary>
/// Thrown when an .rfa, .v3c or .v3m file cannot be read: wrong signature, unsupported version,
/// a count or offset that points outside the file, or a structure that ends early. The message is
/// written for the user ("'x.rfa' is damaged: ...") and names the file, so the app can show it
/// as-is. Every format reader throws this type and nothing else for bad input.
/// </summary>
public class AssetFormatException : Exception
{
    public AssetFormatException(string message) : base(message) { }

    public AssetFormatException(string message, Exception inner) : base(message, inner) { }
}
