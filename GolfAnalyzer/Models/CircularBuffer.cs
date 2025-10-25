namespace GolfAnalyzer.Models
{
    public class CircularBuffer
    {
        private readonly byte[] buffer;
        private int head;
        private int tail;
        private readonly int capacity;
        private int count;
        private readonly object _lock = new();

        public CircularBuffer(int capacity)
        {
            this.capacity = capacity;
            buffer = new byte[capacity];
            head = 0;
            tail = 0;
            count = 0;
        }

        public void Enqueue(byte item)
        {
            lock (_lock)
            {
                buffer[tail] = item;
                tail = (tail + 1) % capacity;
                if (count == capacity)
                {
                    head = (head + 1) % capacity;
                }
                else
                {
                    count++;
                }
            }
        }

        public void EnqueueRange(byte[] items, int offset, int length)
        {
            for (int i = 0; i < length; i++)
                Enqueue(items[offset + i]);
        }

        public bool TryDequeue(out byte item)
        {
            lock (_lock)
            {
                if (count == 0)
                {
                    item = 0;
                    return false;
                }

                item = buffer[head];
                head = (head + 1) % capacity;
                count--;
                return true;
            }
        }

        public int Count { get { lock (_lock) { return count; } } }
        public bool IsEmpty { get { lock (_lock) { return count == 0; } } }
    }
}