namespace GolfAnalyzer.Services
{
    public static class Calculation
    {
        const float gyrDriftX1 = 0, gyrDriftY1 = 0, gyrDriftZ1 = 0;
        const float gyrDriftX2 = 0, gyrDriftY2 = 0, gyrDriftZ2 = 0;

        static Calculation() { }
        public static double[] MagnitudeOfVector(double[] v1, double[] v2, double[] v3)
        {
            if (v1.Length != v2.Length || v1.Length != v3.Length)
            {
                throw new ArgumentException("All input arrays must have the same length.");
            }
            double[] result = new double[v1.Length];
            for (int i = 0; i < v1.Length; i++)
            {
                result[i] = Math.Sqrt(v1[i] * v1[i] + v2[i] * v2[i] + v3[i] * v3[i]);
            }
            return result;
        }
        public static int FindPeak(double[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Data array cannot be null or empty.");
            }
            int peakIndex = 0;
            double peakValue = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                if (data[i] > peakValue)
                {
                    peakValue = data[i];
                    peakIndex = i;
                }
            }
            return peakIndex;
        }
        
        public static double[] GetAngle(EulerAngle check, double[] aX1, double[] aY1, double[] aZ1,
                                double[] gX1, double[] gY1, double[] gZ1,
                                double[] aX2, double[] aY2, double[] aZ2,
                                double[] gX2, double[] gY2, double[] gZ2)
        {
            double gyro_angle1 = 0, gyro_angle2 = 0;
            double pitch_angle1 = 0, pitch_angle2 = 0;
            double yaw_angle1 = 0, yaw_angle2 = 0;
            double[] roll1 = new double[700];
            double[] roll2 = new double[700];
            double[] pitch1 = new double[700];      // Rotation
            double[] pitch2 = new double[700];
            double[] yaw1 = new double[700];
            double[] yaw2 = new double[700];
            double[] delta_roll = new double[700];  // Flexion
            double[] delta_yaw = new double[700];   // Deviation

            for (int i = 0; i < 700; i++)
            {
                roll1[i] = Math.Atan2(-aY1[i], Math.Sqrt(aX1[i] * aX1[i] + aZ1[i] * aZ1[i])) * 180 / 3.14f;
                gyro_angle1 += ((gX1[i] - gyrDriftX1) / 250.0f);
                roll1[i] = 0.02f * roll1[i] + 0.98f * gyro_angle1;

                roll2[i] = Math.Atan2(-aY2[i], Math.Sqrt(aX2[i] * aX2[i] + aZ2[i] * aZ2[i])) * 180 / 3.14f;
                gyro_angle2 += ((gX2[i] - gyrDriftX2) / 250.0f);
                roll2[i] = 0.02f * roll2[i] + 0.98f * gyro_angle2;

                pitch1[i] = Math.Atan2(-aX1[i], Math.Sqrt(aY1[i] * aY1[i] + aZ1[i] * aZ1[i])) * 180 / 3.14f;
                pitch_angle1 += (gY1[i] - gyrDriftY1) / 250.0f;
                pitch1[i] = 0.02f * pitch1[i] + 0.98f * pitch_angle1;

                pitch2[i] = Math.Atan2(-aX2[i], Math.Sqrt(aY2[i] * aY2[i] + aZ2[i] * aZ2[i])) * 180 / 3.14f;
                pitch_angle2 += (gY2[i] - gyrDriftY2) / 250.0f;
                pitch2[i] = 0.02f * pitch2[i] + 0.98f * pitch_angle2;

                yaw2[i] = Math.Atan2(-Math.Sqrt(aX2[i] * aX2[i] + aY2[i] * aY2[i]), aZ2[i]);
                yaw_angle2 += (gZ2[i] - gyrDriftZ2) / 250.0f;
                yaw2[i] = 0.02f * yaw2[i] + 0.98f * yaw_angle2;

                yaw1[i] = Math.Atan2(-Math.Sqrt(aX1[i] * aX1[i] + aY1[i] * aY1[i]), aZ1[i]);
                yaw_angle1 += (gZ1[i] - gyrDriftZ1) / 250.0f;
                yaw1[i] = 0.02f * yaw1[i] + 0.98f * yaw_angle1;

                delta_roll[i] = (roll1[i] - roll2[i])/2.0;
                delta_yaw[i] = (yaw1[i] - yaw2[i]);
            }
            if (check == EulerAngle.Roll)
            {
                return delta_roll;
            }
            else if (check == EulerAngle.Pitch)
            {
                return pitch1;
            }
            else if (check == EulerAngle.Yaw)
            {
                return delta_yaw;
            }
            else if (check == EulerAngle.Shoulder)
            {
                return yaw2;
            }
            else if (check == EulerAngle.Head)
            {
                return pitch1;   
            }
            else if (check == EulerAngle.Trunk)
            {
                return pitch2;
            }
            else if (check == EulerAngle.Left)
            {
                return pitch1;
            }
            else if (check == EulerAngle.Right)
            {
                return pitch2;
            }
            else
            {
                throw new ArgumentException("Invalid Euler angle type.");
            }
        }

    }
}
