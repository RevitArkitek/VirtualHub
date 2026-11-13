namespace RevitVr.Core.Gltf;

/// <summary>
/// Guards the real-world size of a texture repeat.
///
/// The value arrives from Revit as a bare double whose *unit varies* — the same property comes back
/// as feet in one document and millimetres in the next, and reading it as the wrong one puts a
/// texture either 300x or 3x out of scale. When the converted number cannot be a real texture, the
/// honest answer is not to guess a unit but to reject the value and say so, because a plausible
/// wrong scale is far harder to notice than an obviously flat wall.
/// </summary>
public static class TextureScale
{
    /// <summary>No architectural texture repeats faster than this, and few repeat slower than the max.</summary>
    public const float MinMetres = 0.02f;

    /// <summary>
    /// Above this, the number is almost certainly a unit mistake rather than a large pattern: a
    /// wall-sized gradient is the one case that legitimately tiles this slowly, and it is rare.
    /// </summary>
    public const float MaxMetres = 20f;

    /// <summary>A value we cannot believe, replaced by something architectural.</summary>
    public const float FallbackMetres = 1f;

    /// <summary>
    /// Returns a usable tile size in metres. <paramref name="clamped"/> is true when the input was
    /// rejected (out of range, zero, negative or NaN) and the fallback was substituted.
    /// </summary>
    public static float Sanitise(double metres, out bool clamped)
    {
        if (double.IsNaN(metres) || double.IsInfinity(metres) ||
            metres < MinMetres || metres > MaxMetres)
        {
            clamped = true;
            return FallbackMetres;
        }

        clamped = false;
        return (float)metres;
    }
}
