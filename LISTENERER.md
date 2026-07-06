# Listenerer

Teams compliance-recording bot for the call-scoring hackathon. This is a fork of
[LM-Development/aks-sample](https://github.com/LM-Development/aks-sample) (RecordingBot),
stripped of its Kubernetes assumptions and adapted to run as a plain Windows console app
that leaves per-call audio + metadata on disk for the downstream scoring pipeline.

The upstream repo lives untouched at `../listenerer-upstream` for reference; all
Listenerer changes are marked with `// Listenerer:` comments in the source.

## What changed vs upstream

Upstream zipped each call's WAVs into a GUID-named archive under the OS temp folder and
assumed AKS for deployment. Listenerer instead:

| Change | Where |
|---|---|
| Configurable recording sink (`RecordingRootFolder`), local dir or UNC share, defaults to OS temp when unset | `ServiceSetup/AzureSettings.cs` |
| Per-call output folder `<root>/<callId>/`, shared by audio and metadata writers | `Util/RecordingPaths.cs` (new) |
| Loose WAV files instead of temp/GUID zip archives | `Media/AudioProcessor.cs` |
| `callId` threaded through the media pipeline so writers know their folder | `Bot/BotMediaStream.cs` → `Media/MediaStream.cs` → `Media/AudioProcessor.cs` |
| `metadata.json` written per call on termination (direction, times, participants) | `Bot/CallHandler.cs` |
| `.env-template` documenting every setting the bot reads | `RecordingBot.Console/.env-template` |

## Output layout

Each call produces one folder the scoring app can consume directly:

```
<RecordingRootFolder>/
  <callId>/
    <adId>.wav        # one per participant; PCM 16-bit 16 kHz mono
    all.wav           # mixed stream of all speakers
    metadata.json     # written when the call terminates
```

WAV naming (`<adId>`), in order of preference:
1. **AAD object id** — Teams users
2. **Phone number** (e.g. `+15551234567.wav`) — PSTN callers, the 99% case for inbound
3. **`speaker-<id>.wav`** — last-resort fallback when no identity was available; audio is
   never dropped for lack of identity

Names are sanitized for the filesystem; `metadata.json`'s `participants[].adId` uses the
same value, so the join always works. PSTN callers appear as
`{ "adId": "+15551234567", "displayName": "PSTN +15551234567" }`.

**Phone-number suffix:** when the call has a PSTN leg, every single-sided WAV is renamed
at call end with the caller's number — normalized to digits, 10-digit US numbers get a
leading `1` — as `-[<digits>]`:

```
b7e2a9c4-51d8-4e0f-9a3b-6c8d2f419e75-[13098214236].wav   # the finrep
+13098214236-[13098214236].wav                            # the caller
all.wav                                                    # mixed — never suffixed
```

Teams-to-Teams calls (no PSTN leg) keep unsuffixed names. Scoring app: match files by
adId *prefix*. First phone-shaped identity seen wins if a call somehow has two PSTN legs.

`metadata.json` schema (all times UTC):

```json
{
  "callId": "…",
  "direction": "Incoming",
  "observedParticipantId": "…",
  "startTimeUtc": "2026-07-03T14:00:00Z",
  "endTimeUtc": "2026-07-03T14:12:34Z",
  "durationSeconds": 754.2,
  "participants": [
    { "adId": "aad-object-id", "displayName": "Jane Rep" }
  ],
  "recordingFolder": "D:\\recordings\\<callId>",
  "note": "Each participant WAV is named by AAD object id (adId). 'all.wav' is the mixed stream. PSTN callers may lack an adId."
}
```

Notes for the scoring app:
- Match a WAV to a person via `participants[].adId` → `<adId>.wav`.
- PSTN (phone) callers may have no AAD id; their audio is still in `all.wav`.
- If `IsStereo` or a `WAVSampleRate` is set, converted copies land beside the originals.

## Configuration

All settings load from a `.env` file placed **next to the built executable** (DotNetEnv),
using the `AzureSettings__<Property>` convention. Copy
`src/RecordingBot.Console/.env-template` to `.env` and fill it in.

| Key | Purpose |
|---|---|
| `AzureSettings__BotName` | Bot display name from the Azure app registration |
| `AzureSettings__AadAppId` / `AadAppSecret` | App registration credentials |
| `AzureSettings__ServiceDnsName` | Public DNS name Teams reaches the bot on |
| `AzureSettings__CertificateThumbprint` | Thumbprint of the TLS cert in the Windows cert store (LocalMachine) matching the DNS name |
| `AzureSettings__CallSignalingPort` | Local HTTPS port for Teams signaling webhooks (default 9441; map public 443 to it) |
| `AzureSettings__InstanceInternalPort` | Local TCP media port (default 8445) |
| `AzureSettings__InstancePublicPort` | Public port the media port is exposed on |
| `AzureSettings__RecordingRootFolder` | **Listenerer** — recording sink; local dir (`D:\recordings`) or UNC share (`\\nas\recordings`). Blank = OS temp |
| `AzureSettings__IsStereo`, `WAVSampleRate`, `WAVQuality` | Optional audio post-processing |
| `AzureSettings__CaptureEvents`, `PodName`, `MediaFolder`, `EventsFolder` | Diagnostics / upstream leftovers — defaults are fine |

**Media port reality:** ONE media port multiplexes ALL concurrent calls on the instance.
You do not need a port per call, and adding media ports means running additional bot
instances. For the hackathon, one signaling port + one media port is the whole footprint.

## Build

Windows only — `Microsoft.Skype.Bots.Media` is a native win-x64 SDK. Requires the .NET
SDK (builds target `net8.0`; verified with SDK 10.0.109).

```powershell
cd src
dotnet build RecordingBot.Console/RecordingBot.Console.csproj -c Release
```

Output: `src/RecordingBot.Console/bin/Release/net8.0/win-x64/RecordingBot.Console.exe`.
Last validated build: 0 warnings, 0 errors (Model, Services, Console).

## Azure setup checklist (do before hackathon day 1)

The entire Azure footprint — no VMs, storage, or paid SKUs:

1. **Entra app registration** (single tenant):
   - Microsoft Graph **Application** permissions (not Delegated):
     `Calls.AccessMedia.All`, `Calls.JoinGroupCall.All`, `Calls.JoinGroupCallAsGuest.All`
     (upstream's tested set; AccessMedia is the critical one)
   - Tenant-wide **admin consent** via the portal's "Grant admin consent" button
     (skips the upstream doc's manual /adminconsent + redirect-URI dance)
   - **Client secret** (copy value at creation; note expiry)
   - Record: app (client) id → `AzureSettings__AadAppId`, secret → `AzureSettings__AadAppSecret`,
     tenant id (for the policy PowerShell below)
2. **Azure Bot resource** (F0 free tier) linked to that app id:
   - Teams channel enabled, **calling enabled**
   - Calling webhook: `https://<your-dns-name>/api/calling`
   - Skip "Register bot in Microsoft Teams" — policy bots are auto-invited, never called directly
3. **Teams PowerShell wiring** (as Teams admin, per `docs/guides/policy.md`):
   `New-CsOnlineApplicationInstance` → `Sync-CsOnlineApplicationInstance` →
   `New-CsTeamsComplianceRecordingPolicy` → `Set-CsTeamsComplianceRecordingPolicy
   -ComplianceRecordingApplications` → `Grant-CsTeamsComplianceRecordingPolicy` (pilot users only).
   Set the recording application **non-blocking** so a bot outage cannot fail real calls.

## Install / run on the Windows server

Prerequisites outside this repo (see upstream docs in `docs/`):

1. **Azure app registration + bot** — see checklist above and `docs/setup/bot.md`.
2. **Compliance recording policy** — `docs/guides/policy.md` /
   `docs/explanations/recording-bot-policy.md`. Create the application instance, grant
   the policy to the target (test) users via Teams PowerShell.
3. **DNS + certificate** — a public DNS name pointing at the server, and a matching TLS
   cert imported into the LocalMachine store (note its thumbprint).
4. **Firewall/NAT** — inbound public 443 → server port 9441 (signaling), and the public
   media port → 8445 (TCP).

Then on the server:

```powershell
# 1. Copy the Release output folder to the server
# 2. Copy .env-template to .env next to the exe and fill it in
# 3. Run (elevated shell recommended — it binds HTTPS with the store cert)
.\RecordingBot.Console.exe
```

Console prints `RecordingBot: booting`; place a test call from a policy-targeted user
and confirm `<RecordingRootFolder>/<callId>/` appears with WAVs, then `metadata.json`
when the call ends.

## Testing without a real call

`docs/test/end-to-end-testing.md` covers upstream's approach. For local iteration,
`docs/setup/ngrok.md` describes tunneling signaling + media if you want to test from a
dev box before the server is wired up.
