﻿#region 文件说明
/*------------------------------------------------------------------------------
// Copyright © 2018 Granda. All Rights Reserved.
// 苏州广林达电子科技有限公司 版权所有
//------------------------------------------------------------------------------
// File Name: SecsGem
// Author: Ivan JL Zhang    Date: 2018/4/25 15:27:13    Version: 1.0.0
// Description: 
//   
// 
// Revision History: 
// <Author>  		<Date>     	 	<Revision>  		<Modification>
// 	
//----------------------------------------------------------------------------*/
#endregion
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncTCP;
using Secs4Frmk4.Properties;

namespace Secs4Frmk4
{
    public class SecsGem : IDisposable
    {


        #region event
        /// <summary>
        /// HSMS connection sate changed event
        /// </summary>
        public event EventHandler<TEventArgs<ConnectionState>> ConnectionChanged;

        /// <summary>
        /// Primary message received event
        /// </summary>
        public event EventHandler<TEventArgs<PrimaryMessageWrapper>> PrimaryMessageReceived = DefaultPrimaryMessageReceived;
        private static void DefaultPrimaryMessageReceived(object sender, TEventArgs<PrimaryMessageWrapper> _) { }
        #endregion

        #region Properties
        /// <summary>
        /// Connection state
        /// </summary>
        public ConnectionState State { get; private set; }

        /// <summary>
        /// Device Id.
        /// </summary>
        public ushort DeviceId { get; set; } = 0;

        /// <summary>
        /// T3 timer interval 
        /// </summary>
        public int T3 { get; set; } = 40000;

        /// <summary>
        /// T5 timer interval
        /// </summary>
        public int T5 { get; set; } = 10000;

        /// <summary>
        /// T6 timer interval
        /// </summary>
        public int T6 { get; set; } = 5000;

        /// <summary>
        /// T7 timer interval
        /// </summary>
        public int T7 { get; set; } = 10000;

        /// <summary>
        /// T8 timer interval
        /// </summary>
        public int T8 { get; set; } = 5000;

        public bool IsActive { get; }
        public IPAddress IpAddress { get; }
        public int Port { get; }
        #endregion

        #region field
        private readonly ConcurrentDictionary<int, TaskCompletionSourceToken> _replyExpectedMsgs = new ConcurrentDictionary<int, TaskCompletionSourceToken>();

        private readonly Timer _timer7;	// between socket connected and received Select.req timer
        private readonly Timer _timer8;
        private readonly Timer _timerLinkTest;

        private readonly Action _startImpl;
        private readonly Action _stopImpl;

        private readonly SystemByteGenerator _systemByte = new SystemByteGenerator();
        internal int NewSystemId => _systemByte.New();



        private readonly TaskFactory taskFactory = new TaskFactory(TaskScheduler.Default);

        AsyncTcpClient tcpClient;
        AsyncTcpServer tcpServer;

        #endregion

        #region ctor and start/stop methods
        public SecsGem(bool isActive, IPAddress ip, int port)
        {
            IsActive = isActive;
            IpAddress = ip;
            Port = port;

            #region Timer Action
            _timer7 = new Timer(delegate
            {
                Logger.Error($"T7 Timeout: {T7 / 1000} sec.");
                CommunicationStateChanging(ConnectionState.Retry);
            }, null, Timeout.Infinite, Timeout.Infinite);

            _timer8 = new Timer(delegate
            {
                Logger.Error($"T8 Timeout: {T8 / 1000} sec.");
                CommunicationStateChanging(ConnectionState.Retry);
            }, null, Timeout.Infinite, Timeout.Infinite);

            _timerLinkTest = new Timer(delegate
            {
                if (State == ConnectionState.Selected)
                    SendControlMessage(MessageType.LinkTestRequest, NewSystemId);
            }, null, Timeout.Infinite, Timeout.Infinite);

            #endregion

            _startImpl = () =>
            {
                if (IsActive)
                {
                    tcpClient = new AsyncTcpClient(IpAddress, Port);
                    tcpClient.ServerConnected += AsyncTcpClient_ServerConnected;
                    tcpClient.ServerDisconnected += AsyncTcpClient_ServerDisconnected;
                    tcpClient.ServerExceptionOccurred += TcpClient_ServerExceptionOccurred;
                    tcpClient.DatagramReceived += DatagramReceived;
                    tcpClient.RetryInterval = T5;
                    CommunicationStateChanging(ConnectionState.Connecting);
                    tcpClient.Connect();
                }
                else
                {
                    tcpServer = new AsyncTcpServer(IpAddress, Port);
                    tcpServer.ClientConnected += AsyncTcpServer_ClientConnected;
                    tcpServer.ClientDisconnected += AsyncTcpServer_ClientDisconnected;
                    tcpServer.DatagramReceived += DatagramReceived;
                    CommunicationStateChanging(ConnectionState.Connecting);
                    tcpServer.Start();
                }
            };
            _stopImpl = () =>
            {
                if (IsActive)
                {
                    tcpClient.Close();
                    tcpClient.Dispose();
                    tcpClient = null;
                }
                else
                {
                    tcpServer.Stop();
                    tcpServer.Dispose();
                    tcpServer = null;
                }
            };
        }

        public void Start() => new TaskFactory(TaskScheduler.Default).StartNew(_startImpl);

        public void Stop() => new TaskFactory(TaskScheduler.Default).StartNew(_stopImpl);

        private void Reset()
        {
            _timer7.Change(Timeout.Infinite, Timeout.Infinite);
            _timer8.Change(Timeout.Infinite, Timeout.Infinite);
            _timerLinkTest.Change(Timeout.Infinite, Timeout.Infinite);

            _stopImpl?.Invoke();
        }

        #endregion

        #region receive
        private void DatagramReceived(object sender, TcpDatagramReceivedEventArgs<byte[]> e)
        {
            try
            {
                _timer8.Change(Timeout.Infinite, Timeout.Infinite);
                var receivedCount = e.ReceivedCount;
                if (receivedCount == 0)
                {
                    Logger.Error("Received 0 byte.");
                    CommunicationStateChanging(ConnectionState.Retry);
                    return;
                }

                if (_secsDecoder.Decode(receivedCount))
                {

                }

            }
            catch (Exception)
            {

                throw;
            }
        }
        #endregion

        #region connection
        private void AsyncTcpServer_ClientDisconnected(object sender, TcpClientDisConnectedEventArgs e)
        {
            CommunicationStateChanging(ConnectionState.Retry);
        }

        private void AsyncTcpClient_ServerDisconnected(object sender, TcpServerDisconnectedEventArgs e)
        {
            CommunicationStateChanging(ConnectionState.Retry);
        }

        private void AsyncTcpServer_ClientConnected(object sender, TcpClientConnectedEventArgs e)
        {
            CommunicationStateChanging(ConnectionState.Connected);
        }

        private void AsyncTcpClient_ServerConnected(object sender, TcpServerConnectedEventArgs e)
        {
            CommunicationStateChanging(ConnectionState.Connected);
            SendControlMessage(MessageType.SelectRequest, NewSystemId);
        }
        private void TcpClient_ServerExceptionOccurred(object sender, TcpServerExceptionOccurredEventArgs e)
        {
            CommunicationStateChanging(ConnectionState.Retry);
        }

        private void CommunicationStateChanging(ConnectionState state)
        {
            State = state;
            ConnectionChanged?.Invoke(this, new TEventArgs<ConnectionState>(state));

            switch (State)
            {
                case ConnectionState.Connecting:
                    break;
                case ConnectionState.Connected:
#if !DISABLE_TIMER
                    Logger.Info($"Start T7 Timer: {T7 / 1000} sec.");
                    _timer7.Change(T7, Timeout.Infinite);
#endif
                    break;
                case ConnectionState.Selected:
                    _timer7.Change(Timeout.Infinite, Timeout.Infinite);
                    Logger.Info("Stop T7 Timer");
                    break;
                case ConnectionState.Retry:
                    if (IsDisposed)
                        return;
                    Reset();
                    Task.Factory.StartNew(_startImpl);
                    break;
                default:
                    break;
            }
        }
        #endregion

        #region send message
        private void SendControlMessage(MessageType messageType, int systemByte)
        {
            var token = new TaskCompletionSourceToken(ControlMessage, systemByte, messageType);
            if ((byte)messageType % 2 == 1 && messageType != MessageType.SeperateRequest)
            {
                _replyExpectedMsgs[systemByte] = token;
            }

            var bufferList = new List<ArraySegment<byte>>(2)
            {
                ControlMessageLengthBytes,
                new ArraySegment<byte>(new MessageHeader{
                    DeviceId = 0xFFFF,
                    MessageType = messageType,
                    SystemBytes = systemByte
                }.EncodeTo(new byte[10]))
            };

            if (IsActive)
            {
                tcpClient.Send(bufferList);
                SendDataMessageCompleteHandler(tcpClient, token);
            }
            else
            {
                tcpServer.SyncSendToAll(bufferList);
                SendDataMessageCompleteHandler(tcpServer, token);
            }
        }

        private void SendControlMessageHandler(object sender, TaskCompletionSourceToken e)
        {
            Logger.Info("Sent Control Message: " + e.MsgType);
            if (_replyExpectedMsgs.ContainsKey(e.Id))
            {
                if (!e.Task.Wait(T6))
                {
                    Logger.Error($"T6 Timeout: {T6 / 1000} sec.");
                    CommunicationStateChanging(ConnectionState.Retry);
                }
                _replyExpectedMsgs.TryRemove(e.Id, out _);
            }
        }

        internal Task<SecsMessage> SendDataMessageAsync(SecsMessage secsMessage, int systemByte)
        {
            if (State != ConnectionState.Selected)
                throw new SecsException("Device is not selected");

            var token = new TaskCompletionSourceToken(secsMessage, systemByte, MessageType.DataMessage);

            if (secsMessage.ReplyExpected)
                _replyExpectedMsgs[systemByte] = token;

            var header = new MessageHeader()
            {
                S = secsMessage.S,
                F = secsMessage.F,
                ReplyExpected = secsMessage.ReplyExpected,
                DeviceId = DeviceId,
                SystemBytes = systemByte,
            };
            var bufferList = secsMessage.RawDatas.Value;
            bufferList[1] = new ArraySegment<byte>(header.EncodeTo(new byte[10]));
            if (IsActive)
            {
                tcpClient.Send(bufferList);
                SendDataMessageCompleteHandler(tcpClient, token);
            }
            else
            {
                tcpServer.SyncSendToAll(bufferList);
                SendDataMessageCompleteHandler(tcpServer, token);
            }
            return token.Task;
        }

        private void SendDataMessageCompleteHandler(object sender, TaskCompletionSourceToken token)
        {
            if (!_replyExpectedMsgs.ContainsKey(token.Id))
            {
                token.SetResult(null);
                return;
            }
            try
            {
                if (!token.Task.Wait(T3))
                {
                    Logger.Error($"T3 Timeout[id=0x{token.Id:X8}]: {T3 / 1000} sec.");
                    token.SetException(new SecsException(token.MessageSent, Resources.T3Timeout));
                }
            }
            catch (AggregateException) { }
            finally
            {
                _replyExpectedMsgs.TryRemove(token.Id, out _);
            }
        }
        #endregion

        #region dispose
        private const int DisposalNotStarted = 0;
        private const int DisposalComplete = 1;
        private int _disposeStage;
        private readonly StreamDecoder _secsDecoder;
        private static readonly ArraySegment<byte> ControlMessageLengthBytes = new ArraySegment<byte>(new byte[] { 0, 0, 0, 10 });

        public bool IsDisposed => Interlocked.CompareExchange(ref _disposeStage, DisposalComplete, DisposalComplete) == DisposalComplete;

        private static readonly SecsMessage ControlMessage = new SecsMessage(0, 0, String.Empty);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStage, DisposalComplete) != DisposalNotStarted)
                return;

            ConnectionChanged = null;
            if (State == ConnectionState.Selected)
                SendControlMessage(MessageType.SeperateRequest, NewSystemId);
            Reset();
            _timer7.Dispose();
            _timer8.Dispose();
            _timerLinkTest.Dispose();
        }
        #endregion
    }
}
