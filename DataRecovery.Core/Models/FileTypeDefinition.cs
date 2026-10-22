namespace DataRecovery.Core.Models;

/// <summary>
/// Describes a recoverable file type/format that the user can toggle on or off before a scan.
/// The actual detection/parsing logic lives in the matching <c>ISignatureCarver</c> implementation;
/// this record is the lightweight, UI-friendly description of it.
/// </summary>
public sealed record FileTypeDefinition(
    string Id,
    string DisplayName,
    string DefaultExtension,
    FileCategory Category,
    bool SelectedByDefault)
{
    public static readonly FileTypeDefinition Mp4Family = new("mp4-family", "MP4 / MOV (video)", ".mp4", FileCategory.Video, true);
    public static readonly FileTypeDefinition Mkv = new("mkv", "MKV / WebM (video)", ".mkv", FileCategory.Video, true);
    public static readonly FileTypeDefinition Avi = new("avi", "AVI (video)", ".avi", FileCategory.Video, true);
    public static readonly FileTypeDefinition Wmv = new("wmv", "WMV / ASF (video)", ".wmv", FileCategory.Video, true);

    public static readonly FileTypeDefinition Jpeg = new("jpeg", "JPEG image", ".jpg", FileCategory.Image, false);
    public static readonly FileTypeDefinition Png = new("png", "PNG image", ".png", FileCategory.Image, false);
    public static readonly FileTypeDefinition Pdf = new("pdf", "PDF document", ".pdf", FileCategory.Document, false);
    public static readonly FileTypeDefinition Zip = new("zip", "ZIP / DOCX / XLSX archive", ".zip", FileCategory.Archive, false);

    public static IReadOnlyList<FileTypeDefinition> All { get; } = new[]
    {
        Mp4Family, Mkv, Avi, Wmv, Jpeg, Png, Pdf, Zip,
    };
}
