using System.Collections.Immutable;
using UnifierTSL.Commanding.Composition;
using UnifierTSL.Commanding.Endpoints;
using UnifierTSL.Commanding.Execution;

namespace UnifierTSL.Commanding
{
    public sealed record CommandDispatchRequest
    {
        public required CommandEndpointId EndpointId { get; init; }
        public required CommandExecutionContext ExecutionContext { get; init; }
        public required string RawInput { get; init; }
        public IReadOnlyList<string>? CommandPrefixes { get; init; }
    }

    public sealed record CommandDispatchResult
    {
        public bool Handled { get; init; }
        public bool Matched { get; init; }
        public CommandExecutionRequest? ExecutionRequest { get; init; }
        public CommandEndpointRootBinding? Root { get; init; }
        public CommandOutcome? Outcome { get; init; }
    }

    public static class CommandDispatchCoordinator
    {
        /// <summary>命令进入统一分发器时触发，供审计等旁路记录使用。</summary>
        public static event Action<CommandDispatchRequest>? CommandReceived;

        private static readonly Lock PreprocessorLock = new();
        private static ImmutableArray<Func<CommandDispatchRequest, string>> inputPreprocessors = [];

        public static IDisposable RegisterInputPreprocessor(Func<CommandDispatchRequest, string> preprocessor)
        {
            ArgumentNullException.ThrowIfNull(preprocessor);
            lock (PreprocessorLock)
                inputPreprocessors = inputPreprocessors.Add(preprocessor);
            return new PreprocessorRegistration(preprocessor);
        }

        private sealed class PreprocessorRegistration(Func<CommandDispatchRequest, string> preprocessor) : IDisposable
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                lock (PreprocessorLock)
                    inputPreprocessors = inputPreprocessors.Remove(preprocessor);
            }
        }

        private static CommandDispatchRequest ApplyInputPreprocessors(CommandDispatchRequest request)
        {
            var current = request;
            ImmutableArray<Func<CommandDispatchRequest, string>> preprocessors;
            lock (PreprocessorLock)
                preprocessors = inputPreprocessors;

            foreach (var preprocessor in preprocessors)
            {
                var input = preprocessor(current);
                ArgumentNullException.ThrowIfNull(input);
                current = current with { RawInput = input };
            }
            return current;
        }

        public static bool TryCreateExecutionRequest(
            CommandDispatchRequest request,
            out CommandExecutionRequest executionRequest) {
            ArgumentNullException.ThrowIfNull(request);

            executionRequest = null!;
            if (!CommandLineLexer.TryParseCommandLine(request.RawInput, request.CommandPrefixes, out var invokedRoot, out var rawArgumentTokens)) {
                return false;
            }

            executionRequest = new CommandExecutionRequest {
                ExecutionContext = request.ExecutionContext,
                RawInput = request.RawInput,
                InvokedRoot = invokedRoot,
                RawArgumentTokens = rawArgumentTokens,
            };
            return true;
        }

        public static async Task<CommandDispatchResult> DispatchAsync(
            CommandDispatchRequest request,
            CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(request);

            request = ApplyInputPreprocessors(request);
            CommandReceived?.Invoke(request);

            if (!TryCreateExecutionRequest(request, out var executionRequest)) {
                return new CommandDispatchResult {
                    Handled = false,
                    Matched = false,
                };
            }

            var catalog = CommandSystem.GetEndpointCatalog();
            var endpoint = catalog.FindEndpoint(request.EndpointId) ?? throw new InvalidOperationException(GetParticularString(
                "{0} is command endpoint id",
                $"Command endpoint '{request.EndpointId}' is not registered."));
            var root = catalog.FindRoot(request.EndpointId, executionRequest.InvokedRoot);
            if (root is null) {
                return new CommandDispatchResult {
                    Handled = true,
                    Matched = false,
                    ExecutionRequest = executionRequest,
                };
            }

            var outcome = await endpoint.Executor.ExecuteAsync(catalog, root, executionRequest, cancellationToken);
            return new CommandDispatchResult {
                Handled = true,
                Matched = true,
                ExecutionRequest = executionRequest,
                Root = root,
                Outcome = outcome,
            };
        }
    }
}
