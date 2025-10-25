namespace GolfAnalyzer.Models
{
    public class DataPointModel
    {
        public int X { get; set; }
        public float Y { get; set; }
        public DataPointModel(int x, float y)
        {
            X = x;
            Y = y;
        }
    }
}
