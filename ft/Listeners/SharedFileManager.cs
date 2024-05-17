﻿using ft.Commands;
using ft.Listeners;
using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class SharedFileManager : StreamEstablisher
    {
        readonly Dictionary<int, BlockingCollection<byte[]>> ReceiveQueue = [];
        readonly BlockingCollection<Command> SendQueue = new(1);    //using a queue size of one makes the TCP receiver synchronous

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            Task.Factory.StartNew(ReceivePump, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(SendPump, TaskCreationOptions.LongRunning);
        }

        public byte[]? Read(int connectionId)
        {
            if (!ReceiveQueue.TryGetValue(connectionId, out BlockingCollection<byte[]>? queue))
            {
                queue = [];
                ReceiveQueue.Add(connectionId, queue);
            }

            byte[]? result = null;
            try
            {
                result = queue.Take(cancellationTokenSource.Token);
            }
            catch (InvalidOperationException)
            {
                //This is normal - the queue might have been marked as AddingComplete while we were listening
            }

            return result;
        }

        public void Connect(int connectionId)
        {
            var connectCommand = new Connect(connectionId);
            SendQueue.Add(connectCommand);
        }

        public void Write(int connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            SendQueue.Add(forwardCommand);
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);
            SendQueue.Add(teardownCommand);

            ReceiveQueue.Remove(connectionId);
        }

        public void SendPump()
        {
            try
            {
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

                var fileWriter = new BinaryWriter(fileStream);
                var fileReader = new BinaryReader(fileStream, Encoding.ASCII);

                //write messages to disk
                foreach (var message in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    //signal that the batch is not ready to read
                    fileStream.Seek(0, SeekOrigin.Begin);
                    fileWriter.Write((byte)0);

                    //write the message to file
                    message.Serialise(fileWriter);

                    //signal that the batch is ready
                    fileStream.Seek(0, SeekOrigin.Begin);
                    fileWriter.Write((byte)1);
                    fileWriter.Flush();

                    //wait for the counterpart to acknowledge the batch, by setting the first byte to zero
                    fileStream.Seek(0, SeekOrigin.Begin);
                    while (true)
                    {
                        var nextByte = fileReader.PeekChar();

                        if (nextByte == 0)
                        {
                            break;
                        }
                        else
                        {
                            //Console.WriteLine($"[{ReadFromFilename}] Waiting for data at position {fileStream.Position:N0}");
                            fileStream.Flush(); //force read from file

                            Delay.Wait(1);  //avoids a tight loop
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        public ulong TotalBytesReceived = 0;
        public DateTime started = DateTime.Now;

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            try
            {
                var fileStream = new FileStream(ReadFromFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

                var binaryReader = new BinaryReader(fileStream, Encoding.ASCII);
                var binaryWriter = new BinaryWriter(fileStream);

                while (true)
                {
                    while (true)
                    {
                        var nextByte = binaryReader.PeekChar();

                        if (nextByte == -1 || nextByte == 0)
                        {
                            //Console.WriteLine($"[{ReadFromFilename}] Waiting for data at position {fileStream.Position:N0}");
                            fileStream.Flush(); //force read from file
                            Delay.Wait(1);  //avoids a tight loop
                        }
                        else
                        {
                            break;
                        }
                    }

                    //skip the byte that signalled that the batch was ready
                    fileStream.Seek(1, SeekOrigin.Current);


                    var posBeforeCommand = fileStream.Position;

                    var command = Command.Deserialise(binaryReader);

                    if (command == null)
                    {
                        Program.Log($"Could not read command at file position {posBeforeCommand:N0}. [{ReadFromFilename}]", ConsoleColor.Red);
                        Environment.Exit(1);
                    }

                    //if (command is Forward fwd)
                    //{
                    //    TotalBytesReceived += (ulong)(fwd.Payload?.Length ?? 0);

                    //    var rate = TotalBytesReceived * 8 / (double)(DateTime.Now - started).TotalSeconds;

                    //    var ordinals = new[] { "", "K", "M", "G", "T", "P", "E" };
                    //    var ordinal = 0;
                    //    while (rate > 1024)
                    //    {
                    //        rate /= 1024;
                    //        ordinal++;
                    //    }
                    //    var bw = Math.Round(rate, 2, MidpointRounding.AwayFromZero);
                    //    var bwStr = $"{bw} {ordinals[ordinal]}b/s";

                    //    Program.Log($"[Received packet {fwd.PacketNumber:N0}] [File position {posBeforeCommand:N0}] [{fwd.GetType().Name}] [{fwd.Payload?.Length ?? 0:N0} bytes] [{bwStr}]");
                    //}
                    //else
                    //{
                    //    Program.Log($"[Received packet {command.PacketNumber:N0}] [File position {posBeforeCommand:N0}] {command.GetType().Name}");
                    //}

                    if (command is Forward forward)
                    {
                        if (!ReceiveQueue.TryGetValue(forward.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                        {
                            connectionReceiveQueue = [];
                            ReceiveQueue.Add(forward.ConnectionId, connectionReceiveQueue);
                        }

                        if (forward.Payload != null)
                        {
                            connectionReceiveQueue.Add(forward.Payload);

                            //Not working yet. Causes iperf to not finish correctly.
                            //wait for it to be sent to the real server, making the connection synchronous
                            //while (connectionReceiveQueue.Count > 0 && ReceiveQueue.ContainsKey(forward.ConnectionId))
                            //{
                            //    Delay.Wait(1);
                            //}
                        }
                    }
                    else if (command is Connect connect)
                    {
                        if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                        {
                            ReceiveQueue.Add(connect.ConnectionId, []);

                            var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                            StreamEstablished?.Invoke(this, sharedFileStream);
                        }
                    }
                    else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                    {
                        Program.Log($"Was asked to tear down connection {teardown.ConnectionId}");

                        ReceiveQueue.Remove(teardown.ConnectionId);

                        connectionReceiveQueue.CompleteAdding();
                    }


                    //signal that we have processed their message
                    fileStream.Seek(0, SeekOrigin.Begin);
                    binaryWriter.Write((byte)0);
                    fileStream.Seek(0, SeekOrigin.Begin);
                }


            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        public override void Stop()
        {
            /*
            ConnectionIds
                .ForEach(connectionId =>
                {
                    var teardownCommand = new TearDown(connectionId);
                    SendQueue.Add(teardownCommand);
                });

            cancellationTokenSource.Cancel();
            receiveTask.Wait();
            sendTask.Wait();

            try
            {
                Program.Log($"Deleting {ReadFromFilename}");
                File.Delete(ReadFromFilename);
            }
            catch { }

            try
            {
                Program.Log($"Deleting {WriteToFilename}");
                File.Delete(WriteToFilename);
            }
            catch { }
            */
        }

        public string WriteToFilename { get; }
        public string ReadFromFilename { get; }
    }
}
