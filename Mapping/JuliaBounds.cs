namespace SDL_Demo.Mapping;

/// <summary>
/// Coarse CPU estimate of how far the visible "silhouette" of the filled Julia set for z^2 + c
/// extends from the origin, so the mapper can pick a zoom that keeps it on screen.
///
/// Julia sets of z^2 + c are symmetric under z -> -z, so the silhouette is always centred on the
/// origin and only its half-extents matter (and only the upper half-plane needs sampling). Uses
/// a deliberately low iteration count: points that escape slowly still look dark/near-boundary on
/// screen, so counting them as "inside" errs toward a slightly larger box — the safe direction
/// for keeping the shape within the border — and also gives a sensible extent for disconnected
/// (dust) sets, which have no true interior.
/// </summary>
public static class JuliaBounds
{
    private const float Extent = 2.0f;   // the filled Julia set always lies within |z| <= 2.
    private const int GridX = 128;       // ~0.03 cell; coarser grids made the fit zoom visibly step.
    private const int GridY = 64;        // upper half-plane only, by symmetry.
    private const int TestIter = 48;

    /// <summary>Returns (maxAbsX, maxAbsY) of the sampled set, or null if nothing stayed bounded.</summary>
    public static (float MaxX, float MaxY)? Measure(float cx, float cy)
    {
        const float stepX = 2f * Extent / GridX;
        const float stepY = Extent / GridY;
        float maxX = 0f;
        float maxY = 0f;
        bool any = false;

        for (int j = 0; j <= GridY; j++)
        {
            float y0 = j * stepY;
            for (int i = 0; i <= GridX; i++)
            {
                float x0 = -Extent + (i * stepX);
                if (!StaysBounded(x0, y0, cx, cy))
                {
                    continue;
                }
                any = true;
                maxX = Math.Max(maxX, MathF.Abs(x0));
                maxY = Math.Max(maxY, y0);
            }
        }

        // Pad by one cell: the true edge can lie anywhere up to the next sample.
        return any ? (maxX + stepX, maxY + stepY) : null;
    }

    private static bool StaysBounded(float x, float y, float cx, float cy)
    {
        for (int n = 0; n < TestIter; n++)
        {
            float x2 = x * x;
            float y2 = y * y;
            if (x2 + y2 > 4f)
            {
                return false;
            }
            y = (2f * x * y) + cy;
            x = x2 - y2 + cx;
        }
        return true;
    }
}
