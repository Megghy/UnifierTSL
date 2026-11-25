using System.Buffers;
using System.Collections.Concurrent;
using Terraria.Localization;
using Terraria.Net.Sockets;
using TrProtocol.NetPackets;
using UnifierTSL.Performance;

namespace UnifierTSL.Network
{
    public abstract class SocketSender(int clientId = -1) : PacketSender
    {
        private static readonly SocketSendCallback EmptySendCallback = static _ => { };

        public ulong ReceivedBytesCount { get; internal set; }
        public ulong SentBytesCount { get; internal set; }
        public uint ReceivedPacketCount { get; internal set; }
        public uint SentPacketCount { get; internal set; }
        public virtual void ResetDataForNewClient() {
            ReceivedBytesCount = 0;
            SentBytesCount = 0;
            ReceivedPacketCount = 0;
            SentPacketCount = 0;
        }
        public abstract ISocket Socket { get; }
        public virtual void SentData() { }

        internal void CountSentBytes(uint size) {
            SentBytesCount += size;
            SentPacketCount += 1;
            if (clientId < Terraria.Main.maxPlayers && clientId >= 0 && UnifiedServerCoordinator.GetClientCurrentlyServer(clientId) is { } server) {
                var perf = server.Performance;
                perf.CurrentFrameData.SentBytesCount += size;
                perf.CurrentFrameData.SentPacketCount += 1;
            }
            ServerPerformance.Network.Sent(size);
            SentData();
        }

        public sealed override void SendData(byte[] data, int index, int size) {
            ISocket sk = Socket;
            if (!(sk?.IsConnected() ?? false)) {
                return;
            }
            sk.AsyncSend(data, index, size, EmptySendCallback, null);
            CountSentBytes((uint)size);
        }

        public sealed override void SendData(byte[] data, int index, int size, SocketSendCallback callback, object? state = null) {
            ISocket sk = Socket;
            if (!(sk?.IsConnected() ?? false)) {
                return;
            }
            sk.AsyncSend(data, index, size, callback, state);
            CountSentBytes((uint)size);
        }

        protected sealed override byte[] AllocateBuffer(int size) {
            return ArrayPool<byte>.Shared.Rent(size);
        }

        protected sealed override void SendDataAndFreeBuffer(byte[] buffer, int index, int size) {
            ISocket sk = Socket;
            if (!(sk?.IsConnected() ?? false)) {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }
            try {
                sk.AsyncSendNoCopy(buffer, index, size, static state => {
                    ArrayPool<byte>.Shared.Return((byte[])state);
                }, buffer);
            }
            catch {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }
            CountSentBytes((uint)size);
        }

        private const int MaxPooledFreeBufferStates = 4096;
        private static int pooledFreeBufferStateCount;

        private sealed class FreeBufferState {
            public byte[] Buffer = null!;
            public SocketSendCallback Callback = null!;
            public object? State;
            private int _bufferReturned;
            private int _recycled;
            public void Reset(byte[] buffer, SocketSendCallback callback, object? state) {
                Buffer = buffer;
                Callback = callback;
                State = state;
                Volatile.Write(ref _bufferReturned, 0);
                Volatile.Write(ref _recycled, 0);
            }
            public void ReturnBufferOnce() {
                if (Interlocked.Exchange(ref _bufferReturned, 1) != 0)
                    return;
                byte[] buffer = Buffer;
                Buffer = null!;
                if (buffer is not null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
            public void Recycle() {
                if (Interlocked.Exchange(ref _recycled, 1) != 0)
                    return;
                Buffer = null!;
                Callback = null!;
                State = null;
                if (Interlocked.Increment(ref pooledFreeBufferStateCount) <= MaxPooledFreeBufferStates) {
                    FreeBufferStates.Add(this);
                }
                else {
                    Interlocked.Decrement(ref pooledFreeBufferStateCount);
                }
            }
        }
        private static readonly ConcurrentBag<FreeBufferState> FreeBufferStates = new();

        protected sealed override void SendDataAndFreeBuffer(byte[] buffer, int index, int size, SocketSendCallback callback, object? state = null) {
            ISocket sk = Socket;
            if (!(sk?.IsConnected() ?? false)) {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }
            FreeBufferState? callbackState = null;
            try {
                if (FreeBufferStates.TryTake(out var pooled)) {
                    Interlocked.Decrement(ref pooledFreeBufferStateCount);
                    callbackState = pooled;
                }
                else {
                    callbackState = new FreeBufferState();
                }
                callbackState.Reset(buffer, callback, state);
                sk.AsyncSendNoCopy(buffer, index, size, static boxedState => {
                    FreeBufferState state = (FreeBufferState)boxedState;
                    try {
                        state.ReturnBufferOnce();
                        state.Callback(state.State);
                    }
                    finally {
                        state.Recycle();
                    }
                }, callbackState);
            }
            catch {
                if (callbackState is not null) {
                    callbackState.ReturnBufferOnce();
                    callbackState.Recycle();
                }
                else {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return;
            }
            CountSentBytes((uint)size);
        }
        public virtual void Kick(NetworkText reason, bool bg = false) {
            ISocket sk = Socket;
            if (sk is null) {
                return;
            }
            SendDynamicPacket(new Kick(reason));
            if (bg) {
                Console.WriteLine(Language.GetTextValue("CLI.ClientWasBooted", sk.GetRemoteAddress().ToString(), reason));
            }
        }
    }
}
