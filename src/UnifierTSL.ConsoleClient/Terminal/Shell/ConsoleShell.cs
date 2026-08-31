using UnifierTSL.Contracts.Display;
using UnifierTSL.Contracts.Projection;
using UnifierTSL.Contracts.Sessions;
using UnifierTSL.Contracts.Terminal;
using UnifierTSL.Terminal.Runtime;

namespace UnifierTSL.Terminal.Shell
{
    public sealed class ConsoleShell : IDisposable
    {
        private readonly object sync = new();
        private readonly LineEditorSession lineEditorSession = new();
        private readonly ITerminalDevice terminalDevice;
        private readonly TerminalCapabilities terminalCapabilities;
        private readonly TerminalRenderer renderer;
        private readonly TerminalTuiAdapter tuiAdapter = new();
        private readonly TimeProvider timeProvider;
        private readonly ITimer footerAnimationTimer;
        private readonly Queue<(string Text, bool IsAnsi)> deferredLogWrites = [];

        private bool disposed;
        private bool interactionFrameActive;
        private bool readInProgress;
        private int statusScrollOffset;
        private bool hasOutputCursor;
        private bool outputLineContinuation;
        private bool deferredFooterDraw;
        private int outputCursorLeft;
        private int outputCursorTop;
        private long footerAnimationTick;
        private int lastViewportWidth = -1;
        private int lastWrapWidth = -1;
        private long lastViewportChangeTimestamp;

        private TerminalSurfaceRuntimeFrame currentFrame = TerminalSurfaceRuntimeFrame.Empty;
        private TerminalStatusBarState? statusBar;
        private TerminalAnimatedTextPlayback? inputIndicator;
        private const int FooterViewportStabilizationMs = 180;
        private const int InteractiveKeyPollIntervalMs = 25;
        private const int FooterAnimationTicksPerSecond = 60;

        public ConsoleShell()
            : this(new SystemConsoleTerminalDevice(), TimeProvider.System) {
        }

        public ConsoleShell(IConsoleInterceptionBridge bridge)
            : this(new SystemConsoleTerminalDevice(bridge ?? throw new ArgumentNullException(nameof(bridge))), TimeProvider.System) {
        }

        internal ConsoleShell(ITerminalDevice terminalDevice, TimeProvider timeProvider) {
            this.terminalDevice = terminalDevice;
            this.timeProvider = timeProvider;
            terminalCapabilities = terminalDevice.Capabilities;
            renderer = new TerminalRenderer(terminalDevice);
            lastViewportChangeTimestamp = timeProvider.GetTimestamp();
            footerAnimationTimer = timeProvider.CreateTimer(
                static state => ((ConsoleShell)state!).TickFooterAnimation(),
                this,
                TimeSpan.FromSeconds(1d / FooterAnimationTicksPerSecond),
                TimeSpan.FromSeconds(1d / FooterAnimationTicksPerSecond));
        }

        public bool IsInteractive => terminalCapabilities.IsInteractive;

        public bool SupportsVirtualTerminal => terminalCapabilities.SupportsVirtualTerminal;

        public bool IsKeyAvailable() {
            return !disposed && terminalDevice.IsKeyAvailable();
        }

        public ConsoleKeyInfo ReadKey(bool intercept) {
            ThrowIfDisposed();
            return terminalDevice.ReadKey(intercept);
        }

        public void AppendLog(string text, bool isAnsi) {
            if (string.IsNullOrEmpty(text)) {
                return;
            }

            lock (sync) {
                ThrowIfDisposed();
                if (terminalDevice.IsSelectionActive) {
                    deferredLogWrites.Enqueue((text, isAnsi));
                    deferredFooterDraw = true;
                    return;
                }

                AppendLogLocked(text, isAnsi);
            }
        }

        private void AppendLogLocked(string text, bool isAnsi) {

            if (IsInteractive && renderer.FooterDrawn) {
                renderer.ClearFooterArea(SupportsVirtualTerminal);
                if (hasOutputCursor) {
                    int clearedTop = ClampRow(terminalDevice.Cursor.Top);
                    if (!outputLineContinuation) {
                            // When previous output ended with a line terminator, the next write should
                            // always restart from the cleared footer top row at column 0. This prevents
                            // stale cached anchors from drifting into old footer rows.
                            outputCursorTop = clearedTop;
                            outputCursorLeft = 0;
                    }
                    else if (outputCursorTop > clearedTop) {
                            // Footer reservation near buffer bottom can trigger an implicit scroll.
                            // Keep output anchor at/above the cleared row to avoid downward jumps
                            // and phantom blank lines.
                            outputCursorTop = clearedTop;
                    }
                }
            }

            if (IsInteractive) {
                EnsureOutputCursor();
                ClearFreshOutputRow();
            }

            string output = WriteText(text, isAnsi);
            CaptureOutputCursor(output);

            if (IsInteractive && HasFooterTargetLocked()) {
                int? anchorTopRow = hasOutputCursor ? ResolveFooterAnchorRow() : null;
                DrawFooter(anchorTopRow);
            }
        }

        internal void UpdateStatusFrame(TerminalSurfaceRuntimeFrame frame) {
            lock (sync) {
                ThrowIfDisposed();
                if (!TerminalProjectionStatusAdapter.HasVisibleSessionContent(frame)) {
                    ClearStatusFrameLocked();
                    return;
                }

                var indicatorPlayback = CreateAnimatedTextPlayback(
                    frame.InputIndicator,
                    statusBar?.Indicator,
                    footerAnimationTick);
                statusBar = new TerminalStatusBarState(
                    sequence: frame.StatusSequence,
                    frame: frame,
                    indicator: indicatorPlayback);
                RefreshRenderedAnimationLocked(statusBar.Indicator, footerAnimationTick);

                if (!IsInteractive) {
                    return;
                }

                DrawFooter();
            }
        }

        public void ClearStatusFrame() {
            lock (sync) {
                ThrowIfDisposed();
                ClearStatusFrameLocked();
            }
        }

        public void UpdateStatusDocument(ProjectionDocument document, long statusSequence = 0) {
            ArgumentNullException.ThrowIfNull(document);
            var frame = TerminalSurfaceRuntimeFrame.FromSessionStatusDocument(document, statusSequence);
            UpdateStatusFrame(frame);
        }

        public void ClearLog() {
            lock (sync) {
                ThrowIfDisposed();
                if (!IsInteractive) {
                    terminalDevice.Clear();
                    renderer.Reset();
                    ResetOutputCursorState();
                    return;
                }

                var redrawFooter = HasFooterTargetLocked();
                if (!renderer.FooterDrawn) {
                    ClearRows(0, terminalDevice.Viewport.BufferHeight);
                    renderer.Reset();
                    ResetOutputCursorState();
                    SetCursorPositionSafe(0, 0);
                    if (redrawFooter) {
                        DrawFooter();
                    }

                    return;
                }

                int footerTopRow = Math.Max(0, renderer.FooterTopRow);
                renderer.ClearFooterArea(SupportsVirtualTerminal);
                ClearRows(0, footerTopRow);
                ResetOutputCursorState();
                SetCursorPositionSafe(0, 0);
                if (redrawFooter) {
                    DrawFooter();
                }
            }
        }

        public void Clear() {
            lock (sync) {
                ThrowIfDisposed();
                terminalDevice.Clear();
                renderer.Reset();
                ResetOutputCursorState();
            }
        }

        public string RunBufferedEditor(
            ProjectionDocument document,
            long statusSequence = 0,
            bool trim = false,
            CancellationToken cancellationToken = default,
            Func<ClientBufferedEditorState, bool>? onInputStateChanged = null,
            Action<ConsoleKeyInfo>? onKeyPressed = null,
            Action<int>? onActivitySelectionRequested = null,
            Func<ClientBufferedEditorState, string, bool, bool>? onSubmit = null) {
            ArgumentNullException.ThrowIfNull(document);
            var frame = InteractionProjectionRuntimeFrameCompiler.CreateFrame(document, statusSequence);
            return RunBufferedEditor(
                frame,
                trim,
                cancellationToken,
                onInputStateChanged,
                onKeyPressed,
                onActivitySelectionRequested,
                onSubmit);
        }

        internal string RunBufferedEditor(
            TerminalSurfaceRuntimeFrame? frame = null,
            bool trim = false,
            CancellationToken cancellationToken = default,
            Func<ClientBufferedEditorState, bool>? onInputStateChanged = null,
            Action<ConsoleKeyInfo>? onKeyPressed = null,
            Action<int>? onActivitySelectionRequested = null,
            Func<ClientBufferedEditorState, string, bool, bool>? onSubmit = null) {
            frame ??= TerminalSurfaceRuntimeFrame.Empty;
            onSubmit ??= static (_, _, _) => true;
            if (!IsInteractive) {
                throw new InvalidOperationException("Interactive buffered editor requires an interactive terminal.");
            }

            ClientBufferedEditorState? pendingInputState;
            lock (sync) {
                ThrowIfDisposed();
                readInProgress = true;
                BeginInteractionFrameLocked(frame);
                pendingInputState = CaptureInputStateChangedLocked(onInputStateChanged);
            }
            DispatchInputStateChanged(onInputStateChanged, pendingInputState);

            try {
                while (true) {
                    cancellationToken.ThrowIfCancellationRequested();

                    ConsoleKeyInfo keyInfo;
                    try {
                        keyInfo = ReadInteractiveKey(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                        throw;
                    }

                    int? activitySelectionDelta = null;
                    ClientBufferedEditorState? submitInputState = null;
                    string? submittedText = null;
                    bool forceRawSubmit = false;
                    var redrawPending = false;
                    var captureRawKey = false;
                    pendingInputState = null;
                    DispatchKeyPressed(onKeyPressed, keyInfo);
                    lock (sync) {
                        if (lineEditorSession.CapturesRawKeys) {
                            captureRawKey = true;
                        }

                        if (captureRawKey) {
                            continue;
                        }

                        var action = lineEditorSession.ApplyKey(keyInfo);

                        switch (action.Kind) {
                            case EditorActionKind.None:
                                break;

                            case EditorActionKind.Redraw:
                            case EditorActionKind.Autocomplete:
                                pendingInputState = CaptureInputStateChangedLocked(onInputStateChanged);
                                redrawPending = true;
                                break;

                            case EditorActionKind.ScrollStatus:
                                UpdateStatusScroll(action.Delta);
                                DrawFooter();
                                break;

                            case EditorActionKind.SelectActivity:
                                activitySelectionDelta = action.Delta;
                                break;

                            case EditorActionKind.Submit:
                                submitInputState = lineEditorSession.BuildBufferedState();
                                submittedText = action.Payload ?? string.Empty;
                                forceRawSubmit = action.ForceRawSubmit;
                                break;
                        }
                    }

                    var redrawHandledByPublication = DispatchInputStateChanged(onInputStateChanged, pendingInputState);
                    if (redrawPending && !redrawHandledByPublication) {
                        lock (sync) {
                            if (!disposed && readInProgress) {
                                DrawFooter();
                            }
                        }
                    }

                    if (submitInputState is not null) {
                        var shouldComplete = onSubmit(submitInputState, submittedText ?? string.Empty, forceRawSubmit);
                        if (shouldComplete) {
                            lock (sync) {
                                EndInteractionFrameLocked();
                            }

                            var line = submittedText ?? string.Empty;
                            return trim ? line.Trim() : line;
                        }

                        lock (sync) {
                            DrawFooter();
                        }
                    }

                    if (activitySelectionDelta.HasValue && onActivitySelectionRequested is not null) {
                        try {
                            onActivitySelectionRequested(activitySelectionDelta.Value);
                        }
                        catch {
                        }
                    }
                }
            }
            finally {
                lock (sync) {
                    if (interactionFrameActive) {
                        EndInteractionFrameLocked();
                    }
                }
            }
        }

        internal void RunPassiveInteraction(
            TerminalSurfaceRuntimeFrame frame,
            CancellationToken cancellationToken = default) {
            if (!IsInteractive) {
                throw new InvalidOperationException("Passive interaction rendering requires an interactive terminal.");
            }

            lock (sync) {
                ThrowIfDisposed();
                BeginInteractionFrameLocked(frame);
            }

            try {
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            }
            finally {
                lock (sync) {
                    if (interactionFrameActive) {
                        EndInteractionFrameLocked();
                    }
                }
            }
        }

        private ConsoleKeyInfo ReadInteractiveKey(CancellationToken cancellationToken) {
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                if (terminalDevice.IsKeyAvailable()) {
                    return terminalDevice.ReadKey(intercept: true);
                }

                if (cancellationToken.WaitHandle.WaitOne(InteractiveKeyPollIntervalMs)) {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        internal void UpdateReadLineFrame(
            TerminalSurfaceRuntimeFrame frame) {
            lock (sync) {
                ThrowIfDisposed();
                currentFrame = frame;
                UpdateInputIndicatorLocked(currentFrame);

                if (!readInProgress || !IsInteractive) {
                    return;
                }

                lineEditorSession.UpdateSurfaceFrame(frame);
                lineEditorSession.RefreshPreviewPreference();
                DrawFooter();
            }
        }

        public void UpdateReadLineDocument(
            ProjectionDocument document,
            long statusSequence = 0) {
            ArgumentNullException.ThrowIfNull(document);
            var frame = TerminalSurfaceRuntimeFrame.FromInteractionDocument(document, statusSequence);
            UpdateReadLineFrame(frame);
        }

        internal void UpdateBufferedEditorFrame(
            TerminalSurfaceRuntimeFrame? frame) {
            lock (sync) {
                ThrowIfDisposed();
                var nextFrame = frame ?? currentFrame;

                if (!interactionFrameActive || !IsInteractive) {
                    currentFrame = nextFrame;
                    UpdateInputIndicatorLocked(currentFrame);
                    return;
                }

                if (lineEditorSession.ApplyExternalFrame(nextFrame)) {
                    currentFrame = nextFrame;
                }

                UpdateInputIndicatorLocked(currentFrame);
                lineEditorSession.RefreshPreviewPreference();
                DrawFooter();
            }
        }

        public void Dispose() {
            bool disposeTimer = false;
            lock (sync) {
                if (disposed) {
                    return;
                }

                if (IsInteractive && renderer.FooterDrawn) {
                    try {
                        renderer.ClearFooterArea(SupportsVirtualTerminal);
                    }
                    catch {
                    }
                }

                disposed = true;
                interactionFrameActive = false;
                readInProgress = false;
                renderer.Reset();
                disposeTimer = true;
            }

            if (!disposeTimer) {
                return;
            }

            try {
                footerAnimationTimer.Dispose();
            }
            catch {
            }
        }

        private void ThrowIfDisposed() {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        private void ClearStatusFrameLocked() {
            if (statusBar is null) {
                return;
            }

            statusBar = null;
            if (!IsInteractive || !renderer.FooterDrawn) {
                return;
            }

            if (interactionFrameActive) {
                DrawFooter();
                return;
            }

            renderer.ClearFooterArea(SupportsVirtualTerminal);
            // Footer-only teardown should leave output at a clean fresh-line anchor.
            // Without resetting this state, the next log line may reuse a stale cursor
            // snapshot and leave right-side status remnants.
            outputCursorTop = ClampRow(terminalDevice.Cursor.Top);
            outputCursorLeft = 0;
            outputLineContinuation = false;
            hasOutputCursor = true;
        }

        private int ClampRow(int row) {
            return terminalDevice.Viewport.ClampRow(row);
        }

        private void ResetOutputCursorState() {
            hasOutputCursor = false;
            outputLineContinuation = false;
            outputCursorLeft = 0;
            outputCursorTop = 0;
        }

        private void ClearRows(int startRow, int endRowExclusive) {
            var viewport = terminalDevice.Viewport;
            int width = Math.Max(0, viewport.WritableWidth);
            for (int row = Math.Max(0, startRow); row < endRowExclusive && row < viewport.BufferHeight; row++) {
                ClearRow(row, width);
            }
        }

        private void ClearRow(int row, int width) {
            SetCursorPositionSafe(0, row);
            if (SupportsVirtualTerminal) {
                terminalDevice.Write(AnsiColorCodec.Reset);
                terminalDevice.Write("\u001b[2K\r");
                return;
            }

            terminalDevice.Write(new string(' ', width));
            SetCursorPositionSafe(0, row);
        }

        private void SetCursorPositionSafe(int left, int top) {
            try {
                terminalDevice.SetCursorPosition(left, top);
            }
            catch {
            }
        }


        private string WriteText(string text, bool isAnsi) {
            string output = isAnsi ? text : AnsiSanitizer.SanitizeEscapes(text);
            if (!SupportsVirtualTerminal) {
                output = AnsiSanitizer.StripAnsi(output);
            }

            terminalDevice.Write(output);
            return output;
        }

        private void DrawFooter(int? anchorTopRow = null) {
            if (terminalDevice.IsSelectionActive) {
                deferredFooterDraw = true;
                return;
            }

            deferredFooterDraw = false;

            while (deferredLogWrites.TryDequeue(out var deferred)) {
                AppendLogLocked(deferred.Text, deferred.IsAnsi);
            }

            if (ShouldDelayFooterLocked()) {
                return;
            }

            LineEditorRenderState inputState = lineEditorSession.GetRenderState();
            TerminalRenderScene renderScene = tuiAdapter.BuildRenderScene(
                currentFrame,
                inputState,
                statusBar,
                inputIndicator?.RenderedFrame ?? new StyledTextLine(),
                statusScrollOffset,
                terminalDevice.Viewport.WritableWidth);
            statusScrollOffset = renderScene.StatusScrollOffset;
            // Normalize once here so all downstream row math uses the same bounded coordinate space.
            int? normalizedAnchor = anchorTopRow is int requestedAnchor
                ? ClampRow(requestedAnchor)
                : null;
            bool showInputRow = ShouldRenderInputRowLocked();
            renderer.Draw(
                renderScene,
                showInputRow,
                showInputRow,
                SupportsVirtualTerminal,
                normalizedAnchor);

            if (normalizedAnchor is int anchorRow && hasOutputCursor) {
                int actualFooterTop = renderer.FooterTopRow;
                int footerShift = anchorRow - actualFooterTop;
                if (footerShift != 0) {
#if UNIFIER_TERMINAL_DEBUG_TRACE
                    TerminalDebugTrace.WriteThrottled(
                        "ConsoleShell.FooterShift",
                        $"anchor={anchorRow} actualTop={actualFooterTop} shift={footerShift} outputTopBefore={outputCursorTop} outputLeft={outputCursorLeft} continuation={outputLineContinuation} readInProgress={readInProgress} statusLines={renderScene.StatusLines.Count}",
                        minIntervalMs: 80);
#endif
                }

                if (footerShift > 0) {
                    outputCursorTop = ClampRow(outputCursorTop - footerShift);
                }
            }
        }

        private bool ShouldDelayFooterLocked() {
            // Console hosts report width changes before row reflow/cursor relocation fully settle.
            // Clearing the old footer immediately is safe, but redrawing against that transient
            // geometry is not: the new footer anchor can be computed from pre-reflow rows and then
            // applied after reflow, which makes the footer drift or fold into log output while the
            // user is still dragging the window edge.
            TerminalViewport viewport = terminalDevice.Viewport;
            int currentWritableWidth = viewport.WritableWidth;
            int currentWrapWidth = viewport.WrapWidth;
            long nowTick = timeProvider.GetTimestamp();

            if (lastViewportWidth < 0 || lastWrapWidth < 0) {
                lastViewportWidth = currentWritableWidth;
                lastWrapWidth = currentWrapWidth;
                lastViewportChangeTimestamp = nowTick;
                return false;
            }

            if (lastViewportWidth != currentWritableWidth
                || lastWrapWidth != currentWrapWidth) {
                int cursorTop;
                try {
                    cursorTop = ClampRow(terminalDevice.Cursor.Top);
                }
                catch {
                    cursorTop = -1;
                }

#if UNIFIER_TERMINAL_DEBUG_TRACE
                TerminalDebugTrace.WriteThrottled(
                    "ConsoleShell.ViewportChange",
                    $"viewport={lastViewportWidth}/{lastWrapWidth}->{currentWritableWidth}/{currentWrapWidth} cursorTop={cursorTop} readInProgress={readInProgress}",
                    minIntervalMs: 80);
#endif
                if (renderer.FooterDrawn) {
                    // Hide the stale footer immediately so terminal host reflow during drag
                    // cannot visually fold the old footer into unrelated rows before redraw stabilizes.
                    renderer.ClearFooterArea(SupportsVirtualTerminal);
                }
                lastViewportWidth = currentWritableWidth;
                lastWrapWidth = currentWrapWidth;
                lastViewportChangeTimestamp = nowTick;
                return true;
            }

            return timeProvider.GetElapsedTime(lastViewportChangeTimestamp, nowTick) < TimeSpan.FromMilliseconds(FooterViewportStabilizationMs);
        }

        private ClientBufferedEditorState? CaptureInputStateChangedLocked(Func<ClientBufferedEditorState, bool>? onInputStateChanged) {
            if (onInputStateChanged is null) {
                return null;
            }

            // Windowed readline callbacks may synchronously write to the duplex pipe. Keep that I/O
            // outside sync so inbound render packets and footer animation are never blocked behind
            // a backpressured transport write.
            return lineEditorSession.BuildBufferedState();
        }

        private static bool DispatchInputStateChanged(Func<ClientBufferedEditorState, bool>? onInputStateChanged, ClientBufferedEditorState? state) {
            if (onInputStateChanged is null || state is null) {
                return false;
            }

            try {
                return onInputStateChanged(state);
            }
            catch {
                return false;
            }
        }

        private static void DispatchKeyPressed(Action<ConsoleKeyInfo>? onKeyPressed, ConsoleKeyInfo keyInfo) {
            if (onKeyPressed is null) {
                return;
            }

            try {
                onKeyPressed(keyInfo);
            }
            catch {
            }
        }

        private void TickFooterAnimation() {
            lock (sync) {
                footerAnimationTick += 1;
                bool hasFooterTarget = HasFooterTargetLocked();
                if (disposed || !IsInteractive || (!renderer.FooterDrawn && !hasFooterTarget)) {
                    return;
                }

                long animationTick = footerAnimationTick;
                bool shouldRedraw = deferredFooterDraw || !renderer.FooterDrawn && hasFooterTarget;
                if (renderer.FooterDrawn && renderer.RequiresViewportRefresh()) {
                    shouldRedraw = true;
                }

                if (statusBar is not null && RefreshRenderedAnimationLocked(statusBar.Indicator, animationTick)) {
                    shouldRedraw = true;
                }

                if (inputIndicator is not null && RefreshRenderedAnimationLocked(inputIndicator, animationTick)) {
                    shouldRedraw = true;
                }

                if (!shouldRedraw) {
                    return;
                }

                DrawFooter();
            }
        }

        private void BeginInteractionFrameLocked(TerminalSurfaceRuntimeFrame frame) {
            interactionFrameActive = true;
            currentFrame = frame;
            statusScrollOffset = 0;
            UpdateInputIndicatorLocked(currentFrame);
            lineEditorSession.BeginSession(currentFrame);
            lineEditorSession.RefreshPreviewPreference();
            DrawFooter();
        }

        private void EndInteractionFrameLocked() {
            interactionFrameActive = false;
            readInProgress = false;
            renderer.ClearFooterArea(SupportsVirtualTerminal);
            currentFrame = TerminalSurfaceRuntimeFrame.Empty;
            inputIndicator = null;
            if (statusBar is not null) {
                DrawFooter();
            }
        }

        private bool HasFooterTargetLocked() {
            return interactionFrameActive || statusBar is not null;
        }

        private bool ShouldRenderInputRowLocked() {
            return interactionFrameActive && currentFrame.InteractionMode == TerminalSurfaceInteractionMode.Editor;
        }

        private void UpdateInputIndicatorLocked(TerminalSurfaceRuntimeFrame frame) {
            var animatedText = frame.InputIndicator;
            if (animatedText is null || !animatedText.HasVisibleContent) {
                inputIndicator = null;
                return;
            }

            inputIndicator = CreateAnimatedTextPlayback(
                animatedText,
                inputIndicator,
                footerAnimationTick);
            RefreshRenderedAnimationLocked(inputIndicator, footerAnimationTick);
        }

        private static TerminalAnimatedTextPlayback CreateAnimatedTextPlayback(
            TerminalAnimatedText? animatedText,
            TerminalAnimatedTextPlayback? previous,
            long currentTick) {
            StyledTextLine[] normalizedFrames = animatedText?.Frames?
                .Select(static frame => frame ?? new StyledTextLine())
                .ToArray() ?? [];
            int normalizedFrameStepTicks = normalizedFrames.Length <= 1
                ? 0
                : Math.Max(0, animatedText?.FrameStepTicks ?? 0);
            long frameAnchorTick = previous is not null
                && previous.FrameStepTicks == normalizedFrameStepTicks
                && StyledTextLineOps.SequenceEqual(previous.Frames, normalizedFrames)
                    ? previous.FrameAnchorTick
                    : currentTick;
            return new TerminalAnimatedTextPlayback {
                FrameStepTicks = normalizedFrameStepTicks,
                Frames = normalizedFrames,
                FrameAnchorTick = frameAnchorTick,
            };
        }

        private bool RefreshRenderedAnimationLocked(TerminalAnimatedTextPlayback surface, long currentTick) {
            StyledTextLine rendered = ResolveRenderedAnimation(surface, currentTick, out int step);
            bool changed = step != surface.LastFrameStep
                || !StyledTextLineOps.ContentEquals(rendered, surface.RenderedFrame);
            surface.LastFrameStep = step;
            surface.RenderedFrame = rendered;
            return changed;
        }

        private StyledTextLine ResolveRenderedAnimation(
            TerminalAnimatedTextPlayback surface,
            long currentTick,
            out int step) {
            StyledTextLine[] frames = surface.Frames;
            if (frames.Length == 0) {
                step = 0;
                return new StyledTextLine();
            }

            if (surface.FrameStepTicks <= 0) {
                step = 0;
                return frames[0];
            }

            long elapsedTicks = Math.Max(0, currentTick - surface.FrameAnchorTick);
            step = (int)((elapsedTicks / surface.FrameStepTicks) % frames.Length);
            return frames[step];
        }

        private void UpdateStatusScroll(int delta) {
            if (delta == 0) {
                return;
            }

            statusScrollOffset = tuiAdapter.ApplyStatusScrollDelta(
                currentFrame,
                lineEditorSession.GetRenderState(),
                statusBar,
                statusScrollOffset,
                delta);
        }

        private static TerminalSurfaceRuntimeFrame CreateDefaultEditorFrame(
            EditorPaneKind kind,
            EditorKeymap keymap,
            string? initialText = null,
            int initialCaretIndex = 0,
            EditorAuthoringBehavior? authoringBehavior = null) {
            string bufferText = initialText ?? string.Empty;
            return new TerminalSurfaceRuntimeFrame {
                EditorPane = new EditorPaneRuntimeState {
                    Kind = kind,
                    Authority = EditorAuthority.ClientBuffered,
                    AcceptsSubmit = true,
                    Keymap = keymap ?? new EditorKeymap(),
                    AuthoringBehavior = authoringBehavior ?? new EditorAuthoringBehavior(),
                    BufferText = bufferText,
                    CaretIndex = Math.Clamp(initialCaretIndex, 0, bufferText.Length),
                },
            };
        }

        private void EnsureOutputCursor() {
            if (!hasOutputCursor) {
                return;
            }

            try {
                TerminalViewport viewport = terminalDevice.Viewport;
                TerminalCursor currentCursor = terminalDevice.Cursor;
                int safeLeft = viewport.ClampColumn(outputCursorLeft);
                int safeTop = viewport.ClampRow(outputCursorTop);
                int currentTop = viewport.ClampRow(currentCursor.Top);
                if (safeTop > currentTop) {
                    // After clear/scroll, cached anchor can be stale and lower than current visible position.
                    // Clamp to currentTop to avoid jumping downward into empty-looking space.
                    safeTop = currentTop;
                }
                if (currentCursor.Left != safeLeft || currentCursor.Top != safeTop) {
                    terminalDevice.SetCursorPosition(safeLeft, safeTop);
                }

                outputCursorLeft = safeLeft;
                outputCursorTop = safeTop;
            }
            catch {
                hasOutputCursor = false;
                outputLineContinuation = false;
            }
        }

        private void CaptureOutputCursor(string output) {
            if (string.IsNullOrEmpty(output) || !IsInteractive) {
                return;
            }

            try {
                TerminalCursor cursor = terminalDevice.Cursor.Clamp(terminalDevice.Viewport);
                int left = cursor.Left;
                int top = cursor.Top;
                outputCursorLeft = left;
                outputCursorTop = top;
                hasOutputCursor = true;
                bool endedWithLineTerminator = EndsWithLineTerminator(output);
                outputLineContinuation = left != 0 && !endedWithLineTerminator;
            }
            catch {
                hasOutputCursor = false;
                outputLineContinuation = false;
            }
        }

        private void ClearFreshOutputRow() {
            if (outputLineContinuation) {
                return;
            }

            try {
                TerminalViewport viewport = terminalDevice.Viewport;
                int row = viewport.ClampRow(terminalDevice.Cursor.Top);
                terminalDevice.SetCursorPosition(0, row);
                if (SupportsVirtualTerminal) {
                    terminalDevice.Write(AnsiColorCodec.Reset);
                    terminalDevice.Write("\u001b[2K\r");
                }
                else {
                    terminalDevice.Write(new string(' ', viewport.WritableWidth));
                    terminalDevice.SetCursorPosition(0, row);
                }

                outputCursorTop = row;
                outputCursorLeft = 0;
                hasOutputCursor = true;
            }
            catch {
            }
        }

        private int ResolveFooterAnchorRow() {
            int anchorRow = outputCursorTop + (outputLineContinuation ? 1 : 0);
            return ClampRow(anchorRow);
        }

        private static bool EndsWithLineTerminator(string output) {
            // Many log lines end with ANSI reset after CRLF.
            // Reading raw last char ("m") would wrongly treat the line as unterminated.
            // We strip ANSI first, then check the real text ending.
            string normalized = AnsiSanitizer.StripAnsi(output);
            if (normalized.Length == 0) {
                return false;
            }

            char lastChar = normalized[^1];
            return lastChar == '\n' || lastChar == '\r';
        }
    }
}
