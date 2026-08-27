using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System.Diagnostics;
using System.Reflection;
using OTAPI;
using Terraria;
using Terraria.Net;
using Terraria.Net.Sockets;
using Terraria.Testing;
using UnifiedServerProcess;
using UnifierTSL.Extensions;

namespace UnifierTSL.Performance
{
    public partial class ServerPerformance
    {
        public static class Initializer
        {
            public static void Load() { }
            static Initializer() {
                IL.Terraria.Main.mfwh_DedServ += ILDetour_Main_DedServ;

                On.Terraria.Testing.DetailedFPSSystemContext.StartNextFrame += Detour_DetailedFPSSystemContext_StartNextFrame;
                IL.OTAPI.HooksSystemContext.NetMessageSystemContext.InvokeSendBytes += ILDetour_NetMessageSystemContext_InvokeSendBytes;
                IL.OTAPI.HooksSystemContext.NetMessageSystemContext.InvokeSendBytes += ILDetour_InvokeSendBytesNullPath;
                IL.OTAPI.HooksSystemContext.NetMessageSystemContext.InvokeCreatePacketWriter += ILDetour_InvokeCreatePacketWriter;
                IL.Terraria.NetMessage.OnPacketWrite += ILDetour_NetMessage_OnPacketWrite;
                IL.Terraria.NetMessageSystemContext.SendData += ILDetour_NetMessage_SendDataNullPath;
                IL.Terraria.NetMessageSystemContext.SendPacket += ILDetour_NetMessage_SendPacketNullPath;

                IL.Terraria.Net.NetManager.mfwh_Broadcast_NetPacket_BroadcastCondition_int += ILDetour_NetManager_SendData;
                IL.Terraria.Net.NetManager.mfwh_Broadcast_NetPacket_int += ILDetour_NetManager_SendData;
                IL.Terraria.Net.NetManager.mfwh_SendToClient += ILDetour_NetManager_SendData;
            }
            const BindingFlags BF_NonPub_Static = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags BF_NonPub_Instance = BindingFlags.NonPublic | BindingFlags.Instance;
            private static void ILDetour_Main_DedServ(ILContext il) {
                var serverStart = il.Instrs.Single(
                    inst => inst is {
                        OpCode.Code: Code.Callvirt or Code.Call,
                        Operand: MethodReference {
                            Name: nameof(NetplaySystemContext.StartServer), DeclaringType.Name: nameof(NetplaySystemContext)
                        }
                    }
                );
                var cursor = new ILCursor(il);
                cursor.Goto(serverStart, MoveType.After);
                cursor.Emit(OpCodes.Ldarg_0); // this (Main)
                cursor.Emit(OpCodes.Ldarg_1); // root (RootContext)
                cursor.Emit(OpCodes.Call, il.Import(typeof(Initializer).GetMethod(nameof(DedServLoop), BF_NonPub_Static) ?? throw new InvalidOperationException()));
            }
            private static void DedServLoop(Main mainInstance, RootContext root) {
                var server = root.ToServer();
                var Main = server.Main;
                var Netplay = server.Netplay;
                var DetailedFPS = server.DetailedFPS;
                var data = server.Performance;

                var frameTimer = Stopwatch.StartNew();
                var gameTime = new GameTime();
                const double idealFrameTimeMs = 1000 / 60d;
                var nextFrameAtMs = idealFrameTimeMs;

                // Dedicated server doesn't need menus or any of that
                Main.gameMenu = false;

                while (!Netplay.Disconnect)
                {
                    var nowMs = frameTimer.Elapsed.TotalMilliseconds;
                    if (nowMs < nextFrameAtMs)
                    {
                        var sleepMs = (int)Math.Floor(nextFrameAtMs - nowMs) - 1;
                        if (sleepMs > 1)
                            Thread.Sleep(sleepMs);
                        else
                            Thread.Sleep(0);
                        continue;
                    }

                    var scheduledFrameAtMs = nextFrameAtMs;
                    nextFrameAtMs += idealFrameTimeMs;
                    if (nextFrameAtMs <= nowMs)
                        nextFrameAtMs = nowMs;

                    if (Main.oldStatusText != Main.statusText)
                    {
                        Main.oldStatusText = Main.statusText;
                        server.Console.WriteLine(Main.statusText);
                    }

                    // 空服也要推进帧环，否则 console TPS/util 没有采样数据。
                    DetailedFPS.StartNextFrame();
                    if (server.hasRoutedConnections)
                    {
                        try
                        {
                            mainInstance.Update(server, gameTime);
                        }
                        catch (Exception ex)
                        {
                            server.Log.Warning("", ex: ex);
                        }
                    }

                    var frameElapsedMs = frameTimer.Elapsed.TotalMilliseconds - nowMs;
                    var budgetedSleepMs = Math.Max(0d, nextFrameAtMs - frameTimer.Elapsed.TotalMilliseconds);
                    data.CurrentFrameData.SetBudget(
                        Math.Max(0d, nowMs - scheduledFrameAtMs),
                        idealFrameTimeMs,
                        budgetedSleepMs);

                    if (frameElapsedMs <= 0d)
                        Thread.Sleep(0);
                }
            }

            private static void ILDetour_NetManager_SendData(MonoMod.Cil.ILContext il) {
                var call = il.Instrs.First(i => i.Operand is MethodReference { Name: nameof(NetManager.SendData) });
                if (call.Previous.Previous.Previous.OpCode.Code is not Code.Ldelem_Ref ||
                    call.Previous.Previous.Operand is not FieldReference { Name: nameof(Terraria.RemoteClient.Socket) }) {
                    throw new InvalidOperationException();
                }

                var inst = call.Previous.Previous;
                inst.OpCode = OpCodes.Nop;
                inst.Operand = null;
                inst = inst.Previous;
                inst.OpCode = OpCodes.Nop;
                inst.Operand = null;

                call.OpCode = OpCodes.Call;
                call.Operand = il.Import(typeof(Initializer).GetMethod(nameof(SendPacket), BF_NonPub_Static) ?? throw new InvalidOperationException());
            }
            static void SendPacket(NetManager netmanager, RemoteClient[] clients, int clientId, NetPacket packet) {
                netmanager.SendData(clients[clientId].Socket, packet);
                UnifiedServerCoordinator.clientSenders[clientId].CountSentBytes((uint)packet.Writer.BaseStream.Position);
            }


            private static void ILDetour_NetMessageSystemContext_InvokeSendBytes(MonoMod.Cil.ILContext il) {
                var call = il.Instrs.First(i => i.Operand is MethodReference { Name: nameof(ISocket.AsyncSend) });
                il.IL.InsertBefore(call, Instruction.Create(OpCodes.Ldarg_S, il.Method.Parameters.First(p => p.Name is "remoteClient")));
                call.OpCode = OpCodes.Call;
                call.Operand = il.Import(typeof(Initializer).GetMethod(nameof(AsyncSend), BF_NonPub_Static) ?? throw new InvalidOperationException());
            }

            private static void ILDetour_InvokeCreatePacketWriter(ILContext il) {
                var eventField = il.Import(typeof(OTAPI.HooksSystemContext.NetMessageSystemContext)
                    .GetField(nameof(OTAPI.HooksSystemContext.NetMessageSystemContext.CreatePacketWriter), BF_NonPub_Instance)
                    ?? throw new MissingFieldException(nameof(OTAPI.HooksSystemContext.NetMessageSystemContext), nameof(OTAPI.HooksSystemContext.NetMessageSystemContext.CreatePacketWriter)));
                var packetWriterCtor = il.Import(typeof(OTAPI.PacketWriter).GetConstructor([typeof(Stream)])
                    ?? throw new MissingMethodException(typeof(OTAPI.PacketWriter).FullName, ".ctor(Stream)"));
                var original = il.Instrs[0];
                var cursor = new ILCursor(il);
                cursor.Goto(0);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldfld, eventField);
                cursor.Emit(OpCodes.Brtrue, original);
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.Emit(OpCodes.Newobj, packetWriterCtor);
                cursor.Emit(OpCodes.Ret);
            }

            private static void ILDetour_InvokeSendBytesNullPath(ILContext il) {
                var eventField = il.Import(typeof(OTAPI.HooksSystemContext.NetMessageSystemContext)
                    .GetField(nameof(OTAPI.HooksSystemContext.NetMessageSystemContext.SendBytes), BF_NonPub_Instance)
                    ?? throw new MissingFieldException(nameof(OTAPI.HooksSystemContext.NetMessageSystemContext), nameof(OTAPI.HooksSystemContext.NetMessageSystemContext.SendBytes)));
                var original = il.Instrs[0];
                var cursor = new ILCursor(il);
                cursor.Goto(0);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldfld, eventField);
                cursor.Emit(OpCodes.Brtrue, original);
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.Emit(OpCodes.Ldarg_2);
                cursor.Emit(OpCodes.Ldarg_3);
                cursor.Emit(OpCodes.Ldarg, 4);
                cursor.Emit(OpCodes.Ldarg, 5);
                cursor.Emit(OpCodes.Ldarg, 6);
                cursor.Emit(OpCodes.Ldarg, 7);
                cursor.Emit(OpCodes.Call, il.Import(typeof(Initializer).GetMethod(nameof(AsyncSend), BF_NonPub_Static) ?? throw new InvalidOperationException()));
                cursor.Emit(OpCodes.Ret);
            }

            private static void ILDetour_NetMessage_OnPacketWrite(ILContext il) {
                var eventField = il.Import(typeof(HookEvents.Terraria.NetMessage)
                    .GetField(nameof(HookEvents.Terraria.NetMessage.OnPacketWrite), BF_NonPub_Static)
                    ?? throw new MissingFieldException(nameof(HookEvents.Terraria.NetMessage), nameof(HookEvents.Terraria.NetMessage.OnPacketWrite)));
                var original = il.Instrs[0];
                var cursor = new ILCursor(il);
                cursor.Goto(0);
                cursor.Emit(OpCodes.Ldsfld, eventField);
                cursor.Emit(OpCodes.Brtrue, original);
                cursor.Emit(OpCodes.Ret);
            }

            private static void ILDetour_NetMessage_SendDataNullPath(ILContext il)
                => InsertDirectSendPath(
                    il,
                    typeof(HookEvents.Terraria.NetMessage).GetField(nameof(HookEvents.Terraria.NetMessage.SendData), BF_NonPub_Static)
                        ?? throw new MissingFieldException(nameof(HookEvents.Terraria.NetMessage), nameof(HookEvents.Terraria.NetMessage.SendData)),
                    nameof(NetMessageSystemContext.mfwh_SendData),
                    11);

            private static void ILDetour_NetMessage_SendPacketNullPath(ILContext il)
                => InsertDirectSendPath(
                    il,
                    typeof(HookEvents.Terraria.NetMessage).GetField(nameof(HookEvents.Terraria.NetMessage.SendPacket), BF_NonPub_Static)
                        ?? throw new MissingFieldException(nameof(HookEvents.Terraria.NetMessage), nameof(HookEvents.Terraria.NetMessage.SendPacket)),
                    nameof(NetMessageSystemContext.mfwh_SendPacket),
                    2);

            private static void InsertDirectSendPath(ILContext il, FieldInfo eventField, string methodName, int argumentCount) {
                var directMethod = typeof(NetMessageSystemContext).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(NetMessageSystemContext).FullName, methodName);
                var original = il.Instrs[0];
                var cursor = new ILCursor(il);
                cursor.Goto(0);
                cursor.Emit(OpCodes.Ldsfld, il.Import(eventField));
                cursor.Emit(OpCodes.Brtrue, original);
                cursor.Emit(OpCodes.Ldarg_0);
                for (var argument = 1; argument <= argumentCount; argument++)
                    cursor.Emit(OpCodes.Ldarg, argument);
                cursor.Emit(OpCodes.Call, il.Import(directMethod));
                cursor.Emit(OpCodes.Ret);
            }

            static void AsyncSend(ISocket socket, byte[] data, int offset, int size, SocketSendCallback callback, object state, int clientId) {
                socket.AsyncSend(data, offset, size, callback, state);
                UnifiedServerCoordinator.clientSenders[clientId].CountSentBytes((uint)size);
            }

            static void Detour_DetailedFPSSystemContext_StartNextFrame(On.Terraria.Testing.DetailedFPSSystemContext.orig_StartNextFrame orig, DetailedFPSSystemContext self) {
                var server = self.root.ToServer();
                var perf = server.Performance;

                perf.FramesData[self.newest].Finish(server);
                orig(self);
                perf.FramesData[self.newest].Begin(server);
            }
        }
    }
}
