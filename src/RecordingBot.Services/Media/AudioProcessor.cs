using NAudio.Wave;
using RecordingBot.Model.Constants;
using RecordingBot.Services.Contract;
using RecordingBot.Services.ServiceSetup;
using RecordingBot.Services.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RecordingBot.Services.Media
{
    public class AudioProcessor : BufferBase<SerializableAudioMediaBuffer>
    {
        readonly Dictionary<string, WaveFileWriter> _writers = [];
        private readonly string _callId = null;
        private readonly AzureSettings _settings;
        // Listenerer: the call's PSTN number (normalized digits), used to suffix
        // single-sided WAVs at finalize; null when no phone participant was seen.
        private string _callPhoneNumber = null;

        public AudioProcessor(IAzureSettings settings, string callId)
        {
            _callId = callId;
            _settings = (AzureSettings)settings;
        }

        protected override async Task Process(SerializableAudioMediaBuffer data)
        {
            if (data.Timestamp == 0)
            {
                return;
            }

            // Listenerer: write into the configurable per-call folder (<RecordingRootFolder>/<callId>).
            var path = RecordingPaths.GetCallFolder(_settings, _callId);

            // First, write all audio buffer, unless the data.IsSilence is checked for true, into the all speakers buffer
            var all = "all";
            var all_writer = _writers.TryGetValue(all, out WaveFileWriter allWaveWriter) ? allWaveWriter : InitialiseWavFileWriter(path, all);

            if (data.Buffer != null)
            {
                // Buffers are saved to disk even when there is silence.
                // If you do not want this to happen, check if data.IsSilence == true.
                await all_writer.WriteAsync(data.Buffer.AsMemory(0, data.Buffer.Length)).ConfigureAwait(false);
            }

            if (data.SerializableUnmixedAudioBuffers != null)
            {
                foreach (var s in data.SerializableUnmixedAudioBuffers)
                {
                    // Listenerer: never drop a speaker's audio for a missing identity —
                    // 99% of calls are inbound PSTN where the caller has no AAD user.
                    // SerializableUnmixedAudioBuffer guarantees a fallback AdId, but keep
                    // a last-resort default in case a buffer arrives without one.
                    if (s.Buffer == null)
                    {
                        continue;
                    }

                    var id = RecordingPaths.MakeSafeFileName(
                        !string.IsNullOrWhiteSpace(s.AdId) ? s.AdId : $"speaker-{s.ActiveSpeakerId}");

                    // Listenerer: remember the call's PSTN number (first phone-shaped
                    // identity seen) so Finalize can suffix the single-sided WAVs with it.
                    if (_callPhoneNumber == null && LooksLikePhoneNumber(s.AdId))
                    {
                        _callPhoneNumber = NormalizePhoneNumber(s.AdId);
                    }

                    var writer = _writers.TryGetValue(id, out WaveFileWriter bufferWaveWriter) ? bufferWaveWriter : InitialiseWavFileWriter(path, id);

                    // Write audio buffer into the WAV file for individual speaker
                    await writer.WriteAsync(s.Buffer.AsMemory(0, s.Buffer.Length)).ConfigureAwait(false);

                    // Write audio buffer into the WAV file for all speakers
                    await all_writer.WriteAsync(s.Buffer.AsMemory(0, s.Buffer.Length)).ConfigureAwait(false);
                }
            }
        }

        private WaveFileWriter InitialiseWavFileWriter(string rootFolder, string id)
        {
            var path = AudioFileUtils.CreateFilePath(rootFolder, $"{id}.wav");

            // Initialize the Wave Format using the default PCM 16bit 16K supported by Teams audio settings
            var writer = new WaveFileWriter(path, new WaveFormat(
                rate: AudioConstants.DEFAULT_SAMPLE_RATE,
                bits: AudioConstants.DEFAULT_BITS,
                channels: AudioConstants.DEFAULT_CHANNELS));

            _writers.Add(id, writer);

            return writer;
        }

        public async Task<string> Finalize()
        {
            //drain the un-processed buffers on this object
            while (Buffer.Count > 0)
            {
                await Task.Delay(200);
            }

            // Listenerer: leave loose WAV files in the per-call folder (no zip, no temp) so the
            // downstream scoring app can read them directly alongside metadata.json.
            var folder = RecordingPaths.GetCallFolder(_settings, _callId);

            try
            {
                foreach (var entry in _writers)
                {
                    var localFileName = entry.Value.Filename;

                    await entry.Value.FlushAsync();
                    await entry.Value.DisposeAsync();

                    // Optional resample and/or mono-to-stereo conversion; outputs land beside the original.
                    if (_settings.AudioSettings.WavSettings != null)
                    {
                        AudioFileUtils.ResampleAudio(localFileName, _settings.AudioSettings.WavSettings, _settings.IsStereo);
                    }
                    else if (_settings.IsStereo)
                    {
                        AudioFileUtils.ConvertToStereo(localFileName);
                    }

                    // Listenerer: suffix single-sided recordings with the call's PSTN
                    // number, e.g. <adId>-[13098214236].wav. "all" is the mixed stream
                    // and keeps its name; Teams-to-Teams calls (no PSTN leg) get no suffix.
                    if (_callPhoneNumber != null && entry.Key != "all" && File.Exists(localFileName))
                    {
                        var dir = Path.GetDirectoryName(localFileName);
                        var stem = Path.GetFileNameWithoutExtension(localFileName);
                        var suffixed = Path.Combine(dir, $"{stem}-[{_callPhoneNumber}].wav");
                        if (!File.Exists(suffixed))
                        {
                            File.Move(localFileName, suffixed);
                        }
                    }
                }
            }
            finally
            {
                await End();
            }

            return folder;
        }

        // Phone-shaped: "+" followed by digits, or all digits, at least 10 of them.
        // AAD guids (hex + dashes) and speaker-<id> fallbacks never match.
        private static bool LooksLikePhoneNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var v = value.Trim();
            if (v.StartsWith("+"))
            {
                v = v.Substring(1);
            }

            return v.Length >= 10 && v.All(char.IsDigit);
        }

        // Digits only; US 10-digit numbers get the leading country code, giving the
        // 11-digit form used in the filename suffix: -[13098214236]
        private static string NormalizePhoneNumber(string value)
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            return digits.Length == 10 ? "1" + digits : digits;
        }
    }
}
