﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Firefly.Utils;

namespace Firefly.Tests.Fakes
{
    public class FakeSocket : ISocket
    {
        private ArraySegment<byte> _input = new ArraySegment<byte>(new byte[0]);
        private ArraySegment<byte> _output = new ArraySegment<byte>(new byte[0]);
        private readonly object _receiveLock = new object();
        private readonly object _sendLock = new object();

        public FakeSocket()
        {
            Encoding = Encoding.Default;
            OutputWindow = 0x2000;
        }

        public Encoding Encoding { get; set; }
        public int OutputWindow { get; set; }

        public bool ReceiveAsyncPaused { get; set; }
        public SocketAsyncEventArgs ReceiveAsyncArgs { get; set; }

        public bool DisconnectCalled { get; set; }
        public bool ShutdownSendCalled { get; set; }
        public bool ShutdownReceiveCalled { get; set; }


        public string Input
        {
            get { lock (_receiveLock) { return Encoding.GetString(_input.Array, _input.Offset, _input.Count); } }
        }
        public string Output
        {
            get { return Encoding.GetString(_output.Array, _output.Offset, _output.Count); }
        }

        public void Add(string text)
        {
            ContextCallback callback;
            lock (_receiveLock)
            {
                var buffer = new ArraySegment<byte>(Encoding.GetBytes(text));
                var combined = new ArraySegment<byte>(new byte[_input.Count + buffer.Count]);
                Array.Copy(_input.Array, _input.Offset, combined.Array, 0, _input.Count);
                Array.Copy(buffer.Array, buffer.Offset, combined.Array, _input.Count, buffer.Count);
                _input = combined;
                callback = TryReceiveAsync();
            }
            if (callback != null)
            {
                callback(null);
            }
        }

        int TakeInput(ArraySegment<byte> buffer)
        {
            lock (_receiveLock)
            {
                var bytesTransferred = Math.Min(buffer.Count, _input.Count);
                Array.Copy(_input.Array, _input.Offset, buffer.Array, buffer.Offset, bytesTransferred);
                _input = new ArraySegment<byte>(
                    _input.Array,
                    _input.Offset + bytesTransferred,
                    _input.Count - bytesTransferred);
                return bytesTransferred;
            }
        }
        int GiveOutput(ArraySegment<byte> buffer)
        {
            var windowAvailable = OutputWindow - _output.Count;
            var bytesTransfered = Math.Min(buffer.Count, windowAvailable);
            var combined = new ArraySegment<byte>(new byte[_output.Count + bytesTransfered]);
            Array.Copy(_output.Array, _output.Offset, combined.Array, 0, _output.Count);
            Array.Copy(buffer.Array, buffer.Offset, combined.Array, _output.Count, bytesTransfered);
            _output = combined;
            return bytesTransfered;
        }

        ContextCallback TryReceiveAsync()
        {
            lock (_receiveLock)
            {
                if (ReceiveAsyncArgs == null || _input.Count == 0) return null;

                var args = ReceiveAsyncArgs;
                ReceiveAsyncPaused = false;
                ReceiveAsyncArgs = null;

                var bytesTransferred = TakeInput(new ArraySegment<byte>(args.Buffer, args.Offset, args.Count));
                SetField(args, "m_BytesTransferred", bytesTransferred);
                SetField(args, "m_SocketError", SocketError.Success);
                return (ContextCallback)GetField(args, "m_ExecutionCallback");
            }
        }


        // ISocket follows

        public bool Blocking { get; set; }
        public bool NoDelay { get; set; }
        public bool Connected { get; private set; }


        public int Receive(byte[] buffer, int offset, int size, SocketFlags socketFlags, out SocketError errorCode)
        {
            lock (_receiveLock)
            {
                if (ReceiveAsyncPaused)
                    throw new InvalidOperationException("FakeSocket.Receive cannot be called when ReceiveCalled is true");

                var bytesTransferred = TakeInput(new ArraySegment<byte>(buffer, offset, size));
                errorCode = bytesTransferred == 0 ? SocketError.WouldBlock : SocketError.Success;
                return bytesTransferred;
            }
        }

        public bool ReceiveAsync(SocketAsyncEventArgs e)
        {
            lock (_receiveLock)
            {
                if (ReceiveAsyncPaused)
                    throw new InvalidOperationException("FakeSocket.Receive cannot be called when ReceiveCalled is true");

                ReceiveAsyncPaused = true;
                ReceiveAsyncArgs = e;
                return TryReceiveAsync() == null;
            }
        }

        private static void SetField(object obj, string fieldName, object fieldValue)
        {
            var fieldInfo = obj.GetType().GetField(
                fieldName,
                BindingFlags.NonPublic | BindingFlags.SetField | BindingFlags.Instance);
            fieldInfo.SetValue(obj, fieldValue);
        }
        private static object GetField(object obj, string fieldName)
        {
            var fieldInfo = obj.GetType().GetField(
                fieldName,
                BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.Instance);
            return fieldInfo.GetValue(obj);
        }

        public int Send(IList<ArraySegment<byte>> buffers, SocketFlags socketFlags, out SocketError errorCode)
        {
            lock (_sendLock)
            {
                errorCode = SocketError.Success;
                var totalBytes = 0;
                foreach (var buffer in buffers)
                {
                    var bytes = GiveOutput(buffer);
                    totalBytes += bytes;
                    if (bytes != 0 && bytes != buffer.Count)
                    {
                        errorCode = SocketError.WouldBlock;
                    }
                    if (bytes != buffer.Count)
                    {
                        return totalBytes;
                    }
                }
                return totalBytes;
            }
        }


        public bool SendAsync(SocketAsyncEventArgs e)
        {
            lock (_sendLock)
            {
                var buffers = e.BufferList ?? new[] { new ArraySegment<byte>(e.Buffer, e.Offset, e.Count) };

                var byteTransfered = GiveOutput(new ArraySegment<byte>(e.Buffer, e.Offset, e.Count));
                var errorCode = byteTransfered == 0 ? SocketError.IOPending : SocketError.Success;

                SetField(e, "m_SocketError", errorCode);
                SetField(e, "m_BytesTransferred", byteTransfered);
                return errorCode == SocketError.IOPending;
            }
        }


        public void Shutdown(SocketShutdown how)
        {
            switch (how)
            {
                case SocketShutdown.Send:
                    ShutdownSendCalled = true;
                    break;
                case SocketShutdown.Receive:
                    ShutdownReceiveCalled = true;
                    break;
                case SocketShutdown.Both:
                    ShutdownSendCalled = true;
                    ShutdownReceiveCalled = true;
                    break;
            }
        }

        public bool DisconnectAsync(SocketAsyncEventArgs e)
        {
            DisconnectCalled = true;
            return false;
        }

        public void Close()
        {
            throw new NotImplementedException();
        }
    }
}