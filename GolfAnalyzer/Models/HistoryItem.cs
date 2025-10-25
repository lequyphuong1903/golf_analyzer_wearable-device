using System;
using System.Windows.Media;

namespace GolfAnalyzer.Models
{
    public sealed class HistoryItem
    {
        public ImageSource Thumbnail { get; }
        public DateTime SavedTimeUtc { get; }
        public string SavedTimeDisplay => SavedTimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");

        public HistoryItem(ImageSource thumbnail, DateTime savedTimeUtc)
        {
            Thumbnail = thumbnail;
            SavedTimeUtc = savedTimeUtc;
        }
    }
}