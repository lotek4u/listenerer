using RecordingBot.Services.ServiceSetup;
using System.IO;

namespace RecordingBot.Services.Util
{
    /// <summary>
    /// Listenerer: single source of truth for where a call's recordings live.
    /// Both the audio writer (AudioProcessor) and the metadata writer (CallHandler)
    /// derive the same folder from (RecordingRootFolder, callId) so WAVs and
    /// metadata.json always land together.
    /// </summary>
    public static class RecordingPaths
    {
        /// <summary>
        /// Returns (and creates) &lt;RecordingRootFolder&gt;/&lt;callId&gt;.
        /// </summary>
        public static string GetCallFolder(AzureSettings settings, string callId)
        {
            var folder = Path.Combine(settings.RecordingRootFolder, MakeSafeFileName(callId));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Replaces filesystem-hostile characters. Also used for WAV names, which can be
        /// phone numbers or sip-style ids for PSTN callers (":" is invalid on Windows).
        /// </summary>
        public static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown-call";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }

            return value;
        }
    }
}
