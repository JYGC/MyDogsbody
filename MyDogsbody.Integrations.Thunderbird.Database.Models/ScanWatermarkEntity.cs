using LiteDB;

namespace MyDogsbody.Integrations.Thunderbird.Database.Models
{
    /// One folder's watermark: its size and modification time when last read, and the byte
    /// offset reached - keyed by account AccountId and the folder's RelativePath.
    public class ScanWatermarkEntity
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;
        public string? AccountId { get; set; }
        public string? RelativePath { get; set; }
        public long SizeBytes { get; set; }

        /// UTC ticks, NOT a DateTime, and the difference is load-bearing. LiteDB truncates a
        /// stored DateTime to whole milliseconds and hands it back as DateTimeKind.Local, so a
        /// DateTime column could not round-trip File.GetLastWriteTimeUtc: readFolder compares the
        /// loaded value to the file's own mtime with `=`, and a value that came back shifted by
        /// the machine's UTC offset made that comparison fail every time - every folder was
        /// re-read in full on every scan, with nothing on screen to show for it. Ticks are an
        /// int64, so nothing between here and disk can reinterpret them.
        public long ModifiedAtTicksUtc { get; set; }

        public long OffsetReached { get; set; }
    }
}
