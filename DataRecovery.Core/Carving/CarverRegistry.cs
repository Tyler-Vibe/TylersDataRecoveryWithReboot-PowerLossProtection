namespace DataRecovery.Core.Carving;

/// <summary>
/// Central list of all known carvers and the currently-enabled subset (per the user's file-type
/// selection) that a scan pipeline should search for.
/// </summary>
public static class CarverRegistry
{
    public static IReadOnlyList<ISignatureCarver> AllCarvers { get; } =
    [
        new Mp4Carver(),
        new MkvCarver(),
        new AviCarver(),
        new WmvCarver(),
        new JpegCarver(),
        new PngCarver(),
        new PdfCarver(),
        new ZipCarver(),
    ];

    public static List<ISignatureCarver> GetEnabled(IEnumerable<string> selectedFileTypeIds)
    {
        var selected = new HashSet<string>(selectedFileTypeIds, StringComparer.OrdinalIgnoreCase);
        return AllCarvers.Where(c => selected.Contains(c.FileType.Id)).ToList();
    }

    /// <summary>Longest header pattern among the given carvers, used to size the pipeline's chunk lookahead window.</summary>
    public static int MaxPatternLength(IEnumerable<ISignatureCarver> carvers)
    {
        int max = 4;
        foreach (var c in carvers)
        {
            max = Math.Max(max, c.HeaderPattern.Length);
        }

        return max;
    }
}
