using System.Text;
using UnifierTSL.Surface.Activities;
using UnifierTSL.Contracts.Display;
using UnifierTSL.Contracts.Projection;
using UnifierTSL.Servers;

namespace UnifierTSL.Surface.Status
{
    internal enum ActivityCancelRequestResult : byte
    {
        None,
        Requested,
        AlreadyRequested,
    }

    internal sealed class StatusProjectionRuntime : IDisposable
    {
        public const int RefreshIntervalMs = 250;
        public const int IdleRefreshIntervalMs = 5000;

        private readonly ServerContext? server;
        private readonly Action<long, ProjectionDocument> sink;
        private readonly Func<bool> shouldPublish;
        private readonly Lock stateSync = new();
        private readonly Lock publishSync = new();
        private readonly Timer timer;
        private readonly List<ActivityHandle> activeActivities = [];
        private int selectedActivityIndex = -1;
        private string lastSignature = "<unset>";
        private long nextSequence;
        private long lastPeriodicComposeTimestamp;
        private int disposed;

        public StatusProjectionRuntime(
            ServerContext? server,
            Action<long, ProjectionDocument> sink,
            Func<bool>? shouldPublish = null) {

            this.server = server;
            this.sink = sink;
            this.shouldPublish = shouldPublish ?? (() => true);
            timer = new Timer(
                static state => ((StatusProjectionRuntime)state!).PublishCurrent(forcePublish: false, throttleIdle: true),
                this,
                RefreshIntervalMs,
                RefreshIntervalMs);
        }

        public ActivityHandle BeginActivity(
            string category,
            string message,
            ActivityDisplayOptions display = default,
            CancellationToken cancellationToken = default) {
            if (Volatile.Read(ref disposed) != 0) {
                return ActivityHandle.CreateNoop(category, message, display, cancellationToken);
            }

            var activity = new ActivityHandle(
                category,
                message,
                display,
                OnActivityDisposed,
                OnActivityStateChanged,
                cancellationToken);
            if (!TryRegisterActivity(activity)) {
                activity.Dispose();
                return ActivityHandle.CreateNoop(category, message, display, cancellationToken);
            }

            PublishCurrent(forcePublish: false);
            return activity;
        }

        public bool TryCancelCurrentActivity() {
            ActivityHandle? current = null;
            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0 || activeActivities.Count == 0) {
                    return false;
                }

                current = activeActivities[^1];
            }

            return current.RequestCancel();
        }

        public bool TrySelectRelativeActivity(int delta) {
            if (delta == 0) {
                return false;
            }

            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0 || activeActivities.Count == 0) {
                    return false;
                }

                var currentIndex = selectedActivityIndex < 0
                    ? activeActivities.Count - 1
                    : Math.Clamp(selectedActivityIndex, 0, activeActivities.Count - 1);
                var nextIndex = Math.Clamp(currentIndex + delta, 0, activeActivities.Count - 1);
                if (nextIndex == currentIndex) {
                    return false;
                }

                selectedActivityIndex = nextIndex;
            }

            PublishCurrent(forcePublish: false);
            return true;
        }

        public ActivityCancelRequestResult TryCancelSelectedActivity(out ActivityStatusSnapshot? selectedActivity) {
            ActivityHandle? selectedHandle = null;
            selectedActivity = null;

            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0 || activeActivities.Count == 0) {
                    return ActivityCancelRequestResult.None;
                }

                var currentIndex = selectedActivityIndex < 0
                    ? activeActivities.Count - 1
                    : Math.Clamp(selectedActivityIndex, 0, activeActivities.Count - 1);
                selectedActivityIndex = currentIndex;
                selectedHandle = activeActivities[currentIndex];
                selectedActivity = selectedHandle.TryCreateStatusSnapshot();
            }

            if (!selectedActivity.HasValue || selectedHandle is null) {
                return ActivityCancelRequestResult.None;
            }

            if (selectedActivity.Value.IsCancellationRequested) {
                return ActivityCancelRequestResult.AlreadyRequested;
            }

            return selectedHandle.RequestCancel()
                ? ActivityCancelRequestResult.Requested
                : ActivityCancelRequestResult.None;
        }

        public void RepublishCurrent() {
            PublishCurrent(forcePublish: true);
        }

        public bool HasActiveActivity {
            get {
                lock (stateSync) {
                    return Volatile.Read(ref disposed) == 0 && activeActivities.Count > 0;
                }
            }
        }

        public void ResetChangeTracking() {
            lock (stateSync) {
                lastSignature = "<unset>";
            }

            lastPeriodicComposeTimestamp = 0;
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) {
                return;
            }

            try {
                timer.Dispose();
            }
            catch {
            }

            ActivityHandle[] activities;
            lock (stateSync) {
                activities = [.. activeActivities];
                activeActivities.Clear();
                selectedActivityIndex = -1;
                lastSignature = "<unset>";
            }

            foreach (var activity in activities) {
                try {
                    activity.Dispose();
                }
                catch {
                }
            }
        }

        private void PublishCurrent(bool forcePublish, bool throttleIdle = false) {
            long sequence;
            ProjectionDocument document;
            lock (publishSync) {
                if (Volatile.Read(ref disposed) != 0 || !shouldPublish()) {
                    return;
                }

                if (throttleIdle && !forcePublish && ShouldThrottleIdlePublish()) {
                    return;
                }

                if (forcePublish) {
                    document = ComposeCurrentDocument();
                }
                else if (!TryGetCurrentDocumentIfChanged(out document)) {
                    lastPeriodicComposeTimestamp = Environment.TickCount64;
                    return;
                }

                sequence = Interlocked.Increment(ref nextSequence);
                lastPeriodicComposeTimestamp = Environment.TickCount64;
            }

            sink(sequence, document);
        }

        private bool ShouldThrottleIdlePublish() {
            if (HasActiveActivity) {
                return false;
            }

            long last = lastPeriodicComposeTimestamp;
            return last != 0 && Environment.TickCount64 - last < IdleRefreshIntervalMs;
        }

        private ProjectionDocument ComposeCurrentDocument() {
            var baselineDocument = TryComposeBaselineDocument();
            var activityView = TryGetTopActivitySnapshot();
            return StatusProjectionComposer.Compose(baselineDocument, activityView);
        }

        private bool TryGetCurrentDocumentIfChanged(out ProjectionDocument document) {
            document = ComposeCurrentDocument();
            var signature = ComputeVisibleSignature(document);
            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0
                    || string.Equals(signature, lastSignature, StringComparison.Ordinal)) {
                    return false;
                }

                lastSignature = signature;
                return true;
            }
        }

        private static string ComputeVisibleSignature(ProjectionDocument document) {
            var builder = new StringBuilder();
            builder.Append(document.Scope.Kind)
                .Append('\u001f')
                .Append(document.Scope.ScopeId)
                .Append('\u001f')
                .Append(document.Scope.DocumentKind)
                .Append('\u001f')
                .Append(ProjectionStyleDictionaryOps.BuildSignature(document.State.Styles))
                .Append('\n');

            foreach (var node in document.State.Nodes) {
                builder.Append(node.NodeId).Append('\u001f').Append((byte)node.Kind).Append(':');
                switch (node) {
                    case TextProjectionNodeState text:
                        AppendBlock(builder, text.State.Content);
                        break;
                    case DetailProjectionNodeState detail:
                        AppendBlock(builder, detail.State.Heading);
                        builder.Append('|');
                        AppendBlock(builder, detail.State.Summary);
                        foreach (var line in detail.State.Lines) {
                            builder.Append('|');
                            AppendBlock(builder, line);
                        }
                        break;
                    case ContainerProjectionNodeState container:
                        builder.Append(container.State.IsVisible);
                        break;
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static void AppendBlock(StringBuilder builder, ProjectionTextBlock block) {
            foreach (var line in block.Lines) {
                foreach (var span in line.Spans) {
                    builder.Append(span.Text);
                }

                builder.Append('\n');
            }
        }

        private bool TryRegisterActivity(ActivityHandle activity) {
            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0) {
                    return false;
                }

                activeActivities.Add(activity);
                selectedActivityIndex = activeActivities.Count - 1;
                return true;
            }
        }

        private void OnActivityDisposed(ActivityHandle activity) {
            lock (stateSync) {
                var index = activeActivities.IndexOf(activity);
                if (index >= 0) {
                    activeActivities.RemoveAt(index);
                    if (activeActivities.Count == 0) {
                        selectedActivityIndex = -1;
                    }
                    else if (selectedActivityIndex > index) {
                        selectedActivityIndex -= 1;
                    }
                    else if (selectedActivityIndex >= activeActivities.Count || selectedActivityIndex == index) {
                        selectedActivityIndex = Math.Min(index, activeActivities.Count - 1);
                    }
                }
            }

            if (Volatile.Read(ref disposed) == 0) {
                PublishCurrent(forcePublish: false);
            }
        }

        private void OnActivityStateChanged(ActivityHandle activity) {
            if (Volatile.Read(ref disposed) != 0) {
                return;
            }

            PublishCurrent(forcePublish: false);
        }

        private ProjectionDocument? TryComposeBaselineDocument() {
            if (Volatile.Read(ref disposed) != 0) {
                return null;
            }

            var sampleUtc = DateTimeOffset.UtcNow;
            try {
                return RuntimeStatusProjectionProvider.Compose(server, sampleUtc);
            }
            catch {
                return null;
            }
        }

        private ActivityViewSnapshot? TryGetTopActivitySnapshot() {
            lock (stateSync) {
                if (Volatile.Read(ref disposed) != 0 || activeActivities.Count == 0) {
                    return null;
                }

                var currentIndex = selectedActivityIndex < 0
                    ? activeActivities.Count - 1
                    : Math.Clamp(selectedActivityIndex, 0, activeActivities.Count - 1);
                List<ActivityStatusSnapshot> snapshots = [];
                var selectedVisibleIndex = -1;

                for (var i = 0; i < activeActivities.Count; i++) {
                    var snapshot = activeActivities[i].TryCreateStatusSnapshot();
                    if (!snapshot.HasValue) {
                        continue;
                    }

                    if (i == currentIndex) {
                        selectedVisibleIndex = snapshots.Count;
                    }

                    snapshots.Add(snapshot.Value);
                }

                if (snapshots.Count == 0) {
                    return null;
                }

                if (selectedVisibleIndex < 0) {
                    selectedVisibleIndex = snapshots.Count - 1;
                }

                return new ActivityViewSnapshot(
                    snapshots[selectedVisibleIndex],
                    selectedVisibleIndex,
                    [.. snapshots.Select(static snapshot => snapshot.Category)]);
            }
        }
    }
}
