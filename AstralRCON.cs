using MelonLoader;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: MelonInfo(typeof(Astrum.AstralRCON), "AstralRCON", "0.1.0", downloadLink: "github.com/Astrum-Project/AstralRCON")]
[assembly: MelonGame("VRChat", "VRChat")]
[assembly: MelonColor(ConsoleColor.DarkMagenta)]

namespace Astrum
{
    public class AstralRCON : MelonMod
    {
        // this should probably be hashed
        private static string password = "";

        public static List<Socket> connections = new List<Socket>();
        public static Mutex connectionsMutex = new Mutex();

        public override void OnApplicationStart() => Task.Run(Initialize);

        private static void Initialize()
        {
            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 27015));
            listener.Listen(16);

            for (; ; )
            {
                Socket connection = listener.Accept();
                connectionsMutex.WaitOne();
                connections.Add(connection);
                connectionsMutex.ReleaseMutex();
                Task.Run(() =>
                {
                    try { Handle(connection); }
                    catch (Exception ex) { MelonLogger.Msg(ex); }

                    connectionsMutex.WaitOne();
                    connections.Remove(connection);
                    connectionsMutex.ReleaseMutex();
                });
            }
        }

        private static void Handle(Socket socket)
        {
            byte[] buffer = new byte[4096];
            bool isAuthed = false;
            for (; ; )
            {
                int count = socket.Receive(buffer);
                if (count < 10) return;

                Packet packet = new Packet(buffer);

                switch (packet.type)
                {
                    case Packet.PacketType.SERVERDATA_AUTH:
                        if (string.IsNullOrEmpty(password) || packet.body == password)
                            isAuthed = true;
                            
                        socket.Send(new Packet(isAuthed ? packet.id : -1, Packet.PacketType.SERVERDATA_AUTH_RESPONSE, "").ToBytes());
                        break;

                    case Packet.PacketType.SERVERDATA_EXECCOMMAND:
                        if (!isAuthed) return;

                        Evaluate(socket, packet);

                        break;
                }
            }
        }

        private static ConcurrentQueue<(Socket, Packet)> queued = new ConcurrentQueue<(Socket, Packet)>();

        private static void Evaluate(Socket socket, Packet packet)
        {
            queued.Enqueue((socket, packet));
            MelonCoroutines.Start(EvalOnMainThread());
        }

        private static System.Collections.IEnumerator EvalOnMainThread()
        {
            yield return null; // may be unneeded

            if (!queued.TryDequeue(out (Socket, Packet) result))
                yield break;

            result.Item1.Send(new Packet(result.Item2.id, Packet.PacketType.SERVERDATA_RESPONSE_VALUE, AstralCore.Managers.CommandManager.Execute(result.Item2.body)).ToBytes());
        }

        public class Packet
        {
            public int size;
            public int id;
            public PacketType type;
            public string body;

            private byte[] bytes;

            public Packet(int id, PacketType type, string body)
            {
                this.id = id;
                this.type = type;
                this.body = body;
            }

            public Packet(byte[] bytes)
            {
                this.bytes = bytes;

                unsafe
                {
                    fixed (byte* pBytes = bytes)
                    {
                        size = *(int*)pBytes;
                        id = *((int*)pBytes + 1);
                        type = (PacketType)(*((int*)pBytes + 2));

                        //if (*(short*)(pBytes + bytes.Length - 4) != 0)
                        //    throw new Exception();
                    }
                }

                byte[] sbytes = new byte[size - 10];
                Array.Copy(bytes, 12, sbytes, 0, size - 10);
                body = Encoding.UTF8.GetString(sbytes);

                AstralCore.Logger.Trace($"[RCON] {{{size}}} <{id}> ({type}) {body}");
            }

            public byte[] ToBytes()
            {
                int size = body.Length + 10;
                byte[] body_ = Encoding.UTF8.GetBytes(body);
                var list = new List<byte>();

                list.Add((byte)(size & 0x000000FF));
                list.Add((byte)(size & 0x0000FF00));
                list.Add((byte)(size & 0x00FF0000));
                list.Add((byte)(size & 0xFF000000));

                list.Add((byte)(id & 0x000000FF));
                list.Add((byte)(id & 0x0000FF00));
                list.Add((byte)(id & 0x00FF0000));
                list.Add((byte)(id & 0xFF000000));

                list.Add((byte)((int)type & 0x000000FF));
                list.Add((byte)((int)type & 0x0000FF00));
                list.Add((byte)((int)type & 0x00FF0000));
                list.Add((byte)((int)type & 0xFF000000));

                list = list.Concat(body_).ToList();

                list.Add(0);
                list.Add(0);

                return list.ToArray();
            }

            public enum PacketType
            {
                SERVERDATA_RESPONSE_VALUE = 0,
                SERVERDATA_EXECCOMMAND = 2,
                SERVERDATA_AUTH_RESPONSE = 2,
                SERVERDATA_AUTH = 3,
            }
        }
    }
}
