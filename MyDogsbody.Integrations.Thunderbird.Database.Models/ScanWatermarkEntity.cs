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

        /// The ticks of the scan cutoff OffsetReached was reached under - the second half of what
        /// makes a resume sound. Size and modification time alone say only that the file has not
        /// changed; they cannot say that the bytes already passed were examined for the window
        /// being asked about now. Messages older than the cutoff are skipped before their body is
        /// ever parsed, so a wider window needs those same bytes read again.
        ///
        /// Ticks for the same reason as ModifiedAtTicksUtc: this value is COMPARED after loading,
        /// and LiteDB's DateTime round trip truncates to whole milliseconds and shifts the Kind.
        ///
        /// Zero means "not recorded" - a watermark written before this field existed - and
        /// readFolder treats it as unknown rather than as "no cutoff", forcing one full re-read
        /// after which the recorded value is real.
        public long CutoffTicks { get; set; }
    }
}
