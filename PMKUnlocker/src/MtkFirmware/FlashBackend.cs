namespace PMKUnlocker.MtkFirmware;

internal enum FlashOperationState
{
    Idle,
    Connecting,
    ReadingDeviceInfo,
    Authorizing,
    Writing,
    Verifying,
    Completed,
    Failed,
    Interrupted
}

internal static class FlashStateMachine
{
    private static readonly Dictionary<FlashOperationState, FlashOperationState[]> Allowed = new()
    {
        [FlashOperationState.Idle] = new[]
        {
            FlashOperationState.Connecting, FlashOperationState.Failed
        },
        [FlashOperationState.Connecting] = new[]
        {
            FlashOperationState.Idle, FlashOperationState.ReadingDeviceInfo,
            FlashOperationState.Authorizing, FlashOperationState.Writing,
            FlashOperationState.Failed, FlashOperationState.Interrupted
        },
        [FlashOperationState.ReadingDeviceInfo] = new[]
        {
            FlashOperationState.Connecting, FlashOperationState.Authorizing,
            FlashOperationState.Writing, FlashOperationState.Failed,
            FlashOperationState.Interrupted
        },
        [FlashOperationState.Authorizing] = new[]
        {
            FlashOperationState.Connecting, FlashOperationState.Writing,
            FlashOperationState.Failed, FlashOperationState.Interrupted
        },
        [FlashOperationState.Writing] = new[]
        {
            FlashOperationState.Verifying, FlashOperationState.Completed,
            FlashOperationState.Failed, FlashOperationState.Interrupted
        },
        [FlashOperationState.Verifying] = new[]
        {
            FlashOperationState.Completed, FlashOperationState.Failed,
            FlashOperationState.Interrupted
        },
        [FlashOperationState.Completed] = new[]
        {
            FlashOperationState.Idle
        },
        [FlashOperationState.Failed] = new[]
        {
            FlashOperationState.Idle, FlashOperationState.Connecting
        },
        [FlashOperationState.Interrupted] = new[]
        {
            FlashOperationState.Connecting, FlashOperationState.Idle,
            FlashOperationState.Failed
        }
    };

    internal static bool CanTransition(FlashOperationState from, FlashOperationState to)
    {
        if (from == to) return false;
        return Allowed.TryGetValue(from, out FlashOperationState[]? next) && next.Contains(to);
    }

    internal static bool TryTransition(ref FlashOperationState current, FlashOperationState to)
    {
        if (!CanTransition(current, to)) return false;
        current = to;
        return true;
    }
}

internal enum DeviceConnectionState
{
    NotDetected,
    BootromDetected,
    FastbootDetected,
    Lost
}

internal enum ErrorCategory
{
    Connection,
    Driver,
    Loader,
    Authorization,
    Firmware,
    Storage,
    Write,
    Cancelled,
    Unknown
}

internal enum VerificationState
{
    NotPerformed,
    ChecksumVerified,
    PreloaderReadback,
    GptReadback,
    GptAndPreloaderReadback,
    StateVerified
}

internal sealed class FlashPlanItem
{
    public string Partition { get; init; } = "";
    public string Phase { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public long LinearStart { get; init; }
    public RiskLevel Risk { get; init; }
}

internal sealed class FlashPlan
{
    public List<FlashPlanItem> Items { get; } = new();
    public FirmwarePackage? Package { get; set; }
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public IEnumerable<FlashPlanItem> BromItems => Items.Where(i => i.Phase.Equals("BROM", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<FlashPlanItem> FastbootItems => Items.Where(i => i.Phase.Equals("Fastboot", StringComparison.OrdinalIgnoreCase));
}

internal sealed class FlashProgress
{
    public int? Percent { get; init; }
    public string? CurrentPartition { get; init; }
    public long? BytesWritten { get; init; }
    public long? TotalBytes { get; init; }
    public string? Message { get; init; }
}

internal sealed class FlashResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Written { get; } = new();
    public List<(string Partition, ErrorCategory Category, string Message)> Failed { get; } = new();
    public List<(string Partition, string Reason)> Skipped { get; } = new();
    public VerificationState Verification { get; set; } = VerificationState.NotPerformed;
    public string BackendNote { get; set; } = "";
    public bool NeedsRedetection { get; set; }
    public string? DeviceBootState { get; set; }

    public string BuildHonestSummary()
    {
        var lines = new List<string>();
        lines.Add("Result: " + (Cancelled ? "CANCELLED" : Success ? "SUCCESS" : "FAILED"));
        lines.Add("Written: " + (Written.Count > 0 ? string.Join(", ", Written) : "none"));
        if (Failed.Count > 0)
            lines.Add("Failed: " + string.Join(", ",
                Failed.Select(f => f.Partition + " [" + f.Category + "] " + f.Message)));
        if (Skipped.Count > 0)
            lines.Add("Skipped: " + string.Join(", ", Skipped.Select(s => s.Partition + " (" + s.Reason + ")")));
        lines.Add("Verification: " + (Verification switch
        {
            VerificationState.ChecksumVerified => "image checksums verified before write",
            VerificationState.PreloaderReadback => "preloader boot regions readback verified; USER partitions not readback verified",
            VerificationState.GptReadback => "GPT copies readback verified; firmware USER partitions not readback verified",
            VerificationState.GptAndPreloaderReadback => "GPT and preloader readback verified; firmware USER partitions not readback verified",
            VerificationState.StateVerified => "device state verified",
            _ => "not performed — no readback after write"
        }));
        if (!string.IsNullOrEmpty(BackendNote)) lines.Add(BackendNote);
        if (DeviceBootState != null) lines.Add("Boot check: " + DeviceBootState);
        if (NeedsRedetection) lines.Add("Device may need re-detection before further operations.");
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class BackendCapabilities
{
    public string Name { get; init; } = "";
    public bool IsMock { get; init; }
    public bool SupportsFlash { get; init; } = true;
    public bool SupportsReadbackHash { get; init; }
    public bool SupportsFastbootPhase { get; init; } = true;
    public bool SupportsBromPhase { get; init; } = true;
    public bool SupportsPreflightProbe { get; init; }
    public bool SafeCancelDuringWrite { get; init; }
}

internal sealed class PhaseOutcome
{
    public List<string> Written { get; } = new();
    public List<(string Partition, ErrorCategory Category, string Message)> Failed { get; } = new();
    public List<(string Partition, string Reason)> Skipped { get; } = new();
    public bool StopFurtherPhases { get; set; }
    public ErrorCategory? AbortCategory { get; set; }
}

internal interface IMtkBackend
{
    BackendCapabilities Capabilities { get; }
    Task<FlashResult> FlashAsync(FlashPlan plan, IProgress<FlashProgress>? progress, CancellationToken ct);
}

internal sealed class MtkBackendAdapter : IMtkBackend
{
    private readonly Func<FlashPlan, IProgress<FlashProgress>?, CancellationToken, Task<PhaseOutcome>> _bromPhase;
    private readonly Func<FlashPlan, IProgress<FlashProgress>?, CancellationToken, Task<PhaseOutcome>> _fastbootPhase;
    private readonly Func<DeviceProbeResult?>? _probe;

    internal MtkBackendAdapter(
        Func<FlashPlan, IProgress<FlashProgress>?, CancellationToken, Task<PhaseOutcome>> bromPhase,
        Func<FlashPlan, IProgress<FlashProgress>?, CancellationToken, Task<PhaseOutcome>> fastbootPhase,
        Func<DeviceProbeResult?>? probe = null)
    {
        _bromPhase = bromPhase;
        _fastbootPhase = fastbootPhase;
        _probe = probe;
    }

    public BackendCapabilities Capabilities { get; } = new()
    {
        Name = "mtkclient/fastboot (PMKUnlocker)",
        IsMock = false,
        SupportsReadbackHash = false,
        SupportsPreflightProbe = true,
        SafeCancelDuringWrite = false
    };

    public async Task<FlashResult> FlashAsync(FlashPlan plan, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new FlashResult { BackendNote = "Backend: " + Capabilities.Name + ". No post-write readback performed." };
        FlashOperationState state = FlashOperationState.Idle;

        try
        {
            // BROM phase က handoff session (printgpt/reset) မှ run — plan မှာ item ရှိရင် အမြဲတတ် (fastboot-only plan မှာလည်း handoff လို)
            if (plan.Items.Count > 0)
            {
                Set(ref state, FlashOperationState.Connecting);
                Set(ref state, FlashOperationState.Writing);
                progress?.Report(new FlashProgress { Message = "Connecting (BROM phase)…" });
                PhaseOutcome outcome = await _bromPhase(plan, progress, ct);
                Merge(result, outcome);
                Set(ref state, outcome.Failed.Count > 0 ? FlashOperationState.Failed : FlashOperationState.Completed);
                if (outcome.StopFurtherPhases || outcome.Failed.Count > 0)
                {
                    result.NeedsRedetection = outcome.AbortCategory is ErrorCategory.Connection or ErrorCategory.Driver;
                    if (state == FlashOperationState.Completed) Set(ref state, FlashOperationState.Idle);
                    Finish(result);
                    return result;
                }
                if (state == FlashOperationState.Failed)
                    Set(ref state, FlashOperationState.Connecting);
                else
                    Set(ref state, FlashOperationState.Idle);
            }

            if (plan.FastbootItems.Any())
            {
                Set(ref state, FlashOperationState.Connecting);
                Set(ref state, FlashOperationState.Writing);
                progress?.Report(new FlashProgress { Message = "Switching to fastboot phase…" });
                PhaseOutcome outcome = await _fastbootPhase(plan, progress, ct);
                Merge(result, outcome);
                Set(ref state, outcome.Failed.Count > 0 ? FlashOperationState.Failed : FlashOperationState.Completed);
                if (outcome.StopFurtherPhases)
                {
                    result.NeedsRedetection = outcome.AbortCategory is ErrorCategory.Connection or ErrorCategory.Driver;
                    Finish(result);
                    return result;
                }
                else if (state == FlashOperationState.Completed)
                    Set(ref state, FlashOperationState.Idle);
            }

            var expected = plan.Items.Select(i => i.Partition).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.Success = result.Failed.Count == 0 && !result.Cancelled && expected.Count > 0 &&
                             expected.SetEquals(result.Written);
            if (!result.Success && result.Failed.Count == 0)
                result.Failed.Add(("(plan)", ErrorCategory.Write, "Complete write evidence does not match the reviewed plan."));
            Finish(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Success = false;
            result.Skipped.Add(("(remaining)", "cancelled by user"));
            if (FlashStateMachine.CanTransition(state, FlashOperationState.Interrupted))
                state = FlashOperationState.Interrupted;
            result.NeedsRedetection = true;
            Finish(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Failed.Add(("(phase)", Classify(ex), ex.Message));
            if (FlashStateMachine.CanTransition(state, FlashOperationState.Failed))
                state = FlashOperationState.Failed;
            result.NeedsRedetection = true;
            Finish(result);
            return result;
        }
    }

    private static void Set(ref FlashOperationState state, FlashOperationState to)
    {
        if (!FlashStateMachine.TryTransition(ref state, to))
            throw new InvalidOperationException("state machine refused " + state + "→" + to);
    }

    private void Merge(FlashResult result, PhaseOutcome outcome)
    {
        result.Written.AddRange(outcome.Written);
        result.Failed.AddRange(outcome.Failed);
        result.Skipped.AddRange(outcome.Skipped);
    }

    private static void Finish(FlashResult result)
    {
        result.Verification = VerificationState.NotPerformed;
    }

    private static ErrorCategory Classify(Exception ex)
    {
        string m = ex.Message.ToLowerInvariant();
        if (m.Contains("fastboot") || m.Contains("brom") || m.Contains("usb") || m.Contains("connect"))
            return ErrorCategory.Connection;
        if (m.Contains("driver")) return ErrorCategory.Driver;
        if (m.Contains("da ") || m.Contains("loader") || m.Contains("download agent"))
            return ErrorCategory.Loader;
        if (m.Contains("auth") || m.Contains("security")) return ErrorCategory.Authorization;
        if (m.Contains("scatter") || m.Contains("image") || m.Contains("firmware"))
            return ErrorCategory.Firmware;
        if (m.Contains("storage") || m.Contains("emmc") || m.Contains("partition"))
            return ErrorCategory.Storage;
        return ErrorCategory.Unknown;
    }
}

internal sealed class MockMtkBackend : IMtkBackend
{
    private readonly bool _fail;
    private readonly bool _cancelMidway;
    private readonly FlashPlan _observedPlan = new();

    internal MockMtkBackend(bool fail = false, bool cancelMidway = false)
    {
        _fail = fail;
        _cancelMidway = cancelMidway;
    }

    public BackendCapabilities Capabilities { get; } = new()
    {
        Name = "MOCK MtkBackend (test only)",
        IsMock = true,
        SupportsReadbackHash = false,
        SupportsPreflightProbe = false,
        SafeCancelDuringWrite = true
    };

    internal FlashPlan LastPlan => _observedPlan;

    public Task<FlashResult> FlashAsync(FlashPlan plan, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        _observedPlan.Items.Clear();
        _observedPlan.Items.AddRange(plan.Items);

        var result = new FlashResult { BackendNote = "Backend: MOCK (test only) — no device was touched." };

        if (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
            result.Skipped.Add(("(all)", "cancelled before start"));
            result.NeedsRedetection = false;
            return Task.FromResult(result);
        }

        foreach (FlashPlanItem item in plan.Items)
        {
            if (_cancelMidway && result.Written.Count >= 1)
            {
                result.Cancelled = true;
                result.Skipped.Add((item.Partition, "cancelled by test"));
                for (int i = plan.Items.IndexOf(item) + 1; i < plan.Items.Count; i++)
                    result.Skipped.Add((plan.Items[i].Partition, "never started"));
                result.NeedsRedetection = true;
                return Task.FromResult(result);
            }

            progress?.Report(new FlashProgress
            {
                CurrentPartition = item.Partition,
                Message = "flash " + item.Partition,
                BytesWritten = result.Written.Count * 1024,
                TotalBytes = plan.TotalBytes,
                Percent = null
            });

            if (_fail && plan.Items.IndexOf(item) == plan.Items.Count / 2)
            {
                result.Failed.Add((item.Partition, ErrorCategory.Write, "mock write error"));
                foreach (int i in Enumerable.Range(plan.Items.IndexOf(item) + 1,
                             plan.Items.Count - plan.Items.IndexOf(item) - 1))
                    result.Skipped.Add((plan.Items[i].Partition, "skipped after failure"));
                result.Success = false;
                result.NeedsRedetection = false;
                return Task.FromResult(result);
            }
            result.Written.Add(item.Partition);
        }

        result.Success = true;
        result.Verification = VerificationState.NotPerformed;
        return Task.FromResult(result);
    }
}
