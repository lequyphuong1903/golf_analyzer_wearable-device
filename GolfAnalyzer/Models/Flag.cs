namespace GolfAnalyzer.Models
{
    public static class Flag
    {
        static Flag() { }
        public static bool AISkeleton { get; set; } = true;
        public static bool IsLineDrawing { get; set; } = false;
        public static bool IsCircleDrawing { get; set; } = false;
        public static bool IsRectangleDrawing { get; set; } = false;

    }
}
