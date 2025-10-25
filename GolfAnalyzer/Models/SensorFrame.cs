namespace GolfAnalyzer.Models;

public readonly struct SensorFrame
{
    public readonly short aX1, aY1, aZ1, gX1, gY1, gZ1;
    public readonly short aX2, aY2, aZ2, gX2, gY2, gZ2;

    public SensorFrame(short ax1, short ay1, short az1, short gx1, short gy1, short gz1,
                       short ax2, short ay2, short az2, short gx2, short gy2, short gz2)
    {
        aX1 = ax1; aY1 = ay1; aZ1 = az1; gX1 = gx1; gY1 = gy1; gZ1 = gz1;
        aX2 = ax2; aY2 = ay2; aZ2 = az2; gX2 = gx2; gY2 = gy2; gZ2 = gz2;
    }
}