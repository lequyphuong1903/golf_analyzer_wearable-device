using GolfAnalyzer.Models;

namespace GolfAnalyzer.Services
{
    public interface ISerialSensorService : IDisposable
    {
        bool IsConnected { get; }
        event EventHandler<SensorFrameEventArgs>? FrameReceived;

        void Connect(params string[] portNames); // ví dụ: "COM29","COM30","COM31"
        void Disconnect();
    }

    public sealed class SensorFrameEventArgs : EventArgs
    {
        public int SensorId { get; }
        public SensorFrame Frame { get; }

        public SensorFrameEventArgs(int sensorId, SensorFrame frame)
        {
            SensorId = sensorId;
            Frame = frame;
        }
    }
}

