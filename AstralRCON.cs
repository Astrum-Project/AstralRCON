using Astrum.AstralCore.Managers;
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

[assembly: MelonInfo(typeof(Astrum.AstralRCON), nameof(Astrum.AstralRCON), "0.1.1", downloadLink: "github.com/Astrum-Project/" + nameof(Astrum.AstralRCON))]
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

        public override void OnApplicationStart()
        {
            Task.Run(Initialize);

            ModuleManager.Module module = new ModuleManager.Module("RCON");
            module.Register("password", new CommandManager.ConVar<string>(new Action<string>(value => {
                password = value;
                AstralCore.Logger.Info("[RCON] Password changed");
                connections.ForEach(x => x.Close());
            })));
        }

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

            string res;
            try
            {
                res = CommandManager.Execute(result.Item2.body);
            } 
            catch (Exception ex)
            {
                AstralCore.Logger.Trace("\x1b[33m" + ex + "\x1b[0m");
                res = "Exception occurred while running command";
            }

            result.Item1.Send(new Packet(result.Item2.id, Packet.PacketType.SERVERDATA_RESPONSE_VALUE, res).ToBytes());
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
                List<byte> list = new List<byte>
                {
                    (byte)(size & 0x000000FF),
                    (byte)(size & 0x0000FF00),
                    (byte)(size & 0x00FF0000),
                    (byte)(size & 0xFF000000),

                    (byte)(id & 0x000000FF),
                    (byte)(id & 0x0000FF00),
                    (byte)(id & 0x00FF0000),
                    (byte)(id & 0xFF000000),

                    (byte)((int)type & 0x000000FF),
                    (byte)((int)type & 0x0000FF00),
                    (byte)((int)type & 0x00FF0000),
                    (byte)((int)type & 0xFF000000)
                };

                list = list.Concat(Encoding.UTF8.GetBytes(body)).ToList();

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
