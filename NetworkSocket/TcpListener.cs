﻿using NetworkSocket.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace NetworkSocket
{
    /// <summary>
    /// 表示Tcp监听服务
    /// </summary>
    public class TcpListener : IListener
    {
        /// <summary>
        /// 用于监听的socket
        /// </summary>
        private volatile Socket listenSocket;

        /// <summary>
        /// 接受参数
        /// </summary>
        private SocketAsyncEventArgs acceptArg = new SocketAsyncEventArgs();

        /// <summary>
        /// 会话管理器
        /// </summary>
        private TcpSessionManager sessionManager = new TcpSessionManager();

        /// <summary>
        /// 插件管理器
        /// </summary>
        private PlugManager plugManager = new PlugManager();

        /// <summary>
        /// 协议中间件管理器
        /// </summary>
        private MiddlewareManager middlewareManager = new MiddlewareManager();


        /// <summary>
        /// 获取或设置会话的心跳检测时间间隔
        /// TimeSpan.Zero为不检测
        /// </summary>
        public TimeSpan KeepAlivePeriod { get; set; }

        /// <summary>
        /// 获取是否已处在监听中
        /// </summary>
        public bool IsListening { get; private set; }

        /// <summary>
        /// 获取所监听的本地IP和端口
        /// </summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>
        /// 获取服务器证书
        /// </summary>
        public X509Certificate Certificate { get; private set; }

        /// <summary>
        /// Tcp监听服务
        /// </summary>
        public TcpListener()
        {
        }


        /// <summary>
        /// 使用SSL安全传输
        /// </summary>
        /// <param name="cer">证书</param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public void UseSSL(X509Certificate cer)
        {
            if (cer == null)
            {
                throw new ArgumentNullException();
            }
            if (this.IsListening == true)
            {
                throw new InvalidOperationException("实例已经IsListening，不能UseSSL");
            }
            this.Certificate = cer;
        }


        /// <summary>
        /// 使用协议中间件
        /// </summary>
        /// <typeparam name="TMiddleware">中间件类型</typeparam>
        /// <returns></returns>
        public TMiddleware Use<TMiddleware>() where TMiddleware : IMiddleware
        {
            var middleware = Activator.CreateInstance<TMiddleware>();
            this.middlewareManager.Use(middleware);
            return middleware;
        }

        /// <summary>
        /// 使用协议中间件
        /// </summary>
        /// <param name="middleware">协议中间件</param>
        /// <exception cref="ArgumentNullException"></exception>
        public void Use(IMiddleware middleware)
        {
            this.middlewareManager.Use(middleware);
        }


        /// <summary>
        /// 使用插件
        /// </summary>
        /// <typeparam name="TPlug">插件类型</typeparam>
        /// <returns></returns>
        public TPlug UsePlug<TPlug>() where TPlug : IPlug
        {
            var plug = Activator.CreateInstance<TPlug>();
            this.plugManager.Use(plug);
            return plug;
        }

        /// <summary>
        /// 使用插件
        /// </summary>
        /// <param name="plug">插件</param>
        /// <exception cref="ArgumentNullException"></exception>
        public void UsePlug(IPlug plug)
        {
            this.plugManager.Use(plug);
        }

        /// <summary>
        /// 开始启动监听
        /// 如果IsListening为true，将不产生任何作用
        /// </summary>
        /// <param name="port">本机tcp端口</param>
        /// <exception cref="SocketException"></exception>
        public void Start(int port)
        {
            var backlog = 128;
            this.Start(port, backlog);
        }

        /// <summary>
        /// 开始启动监听
        /// 如果IsListening为true，将不产生任何作用
        /// </summary>
        /// <param name="port">本机tcp端口</param>
        /// <param name="backlog">挂起连接队列的最大长度</param>
        /// <exception cref="SocketException"></exception>
        public void Start(int port, int backlog)
        {
            var localEndPoint = new IPEndPoint(IPAddress.Any, port);
            this.Start(localEndPoint, backlog);
        }

        /// <summary>
        /// 开始启动监听
        /// 如果IsListening为true，将不产生任何作用
        /// </summary>
        /// <param name="localEndPoint">本机ip和端口</param>
        /// <param name="backlog">挂起连接队列的最大长度</param>
        /// <exception cref="SocketException"></exception>
        public void Start(IPEndPoint localEndPoint, int backlog)
        {
            if (this.IsListening == true)
            {
                return;
            }
            this.listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this.listenSocket.Bind(localEndPoint);
            this.listenSocket.Listen(backlog);

            this.acceptArg = new SocketAsyncEventArgs();
            this.acceptArg.Completed += (sender, e) => this.EndAccept(e);
            this.BeginAccept(this.acceptArg);

            this.LocalEndPoint = localEndPoint;
            this.IsListening = true;
        }

        /// <summary>
        /// 开始一次接受连接请求操作
        /// </summary>
        /// <param name="arg">接受参数</param>     
        private void BeginAccept(SocketAsyncEventArgs arg)
        {
            if (this.listenSocket != null)
            {
                arg.AcceptSocket = null;
                if (this.listenSocket.AcceptAsync(arg) == false)
                {
                    this.EndAccept(arg);
                }
            }
        }

        /// <summary>
        /// 连接请求IO完成
        /// </summary>
        /// <param name="arg">连接参数</param>
        private void EndAccept(SocketAsyncEventArgs arg)
        {
            var socket = arg.AcceptSocket;
            var error = arg.SocketError;

            this.ProcessAccept(socket, error);
            this.BeginAccept(arg);
        }

        /// <summary>
        /// 处理Socket连接
        /// </summary>
        /// <param name="socket">socket</param>
        /// <param name="socketError">状态</param>
        private void ProcessAccept(Socket socket, SocketError socketError)
        {
            if (socketError == SocketError.Success)
            {
                var session = this.GenerateSession(socket);
                this.StartLoopReceive(session);
            }
            else
            {
                var exception = new SocketException((int)socketError);
                this.plugManager.RaiseException(this, exception);
            }
        }

        /// <summary>
        /// 生成一个会话对象
        /// </summary>
        /// <param name="socket">要绑定的socket</param>
        /// <returns></returns>
        private TcpSessionBase GenerateSession(Socket socket)
        {
            // 创建会话，绑定处理委托
            var session = this.sessionManager.Alloc(this.Certificate);
            session.ReceiveAsyncHandler = this.OnRequestAsync;
            session.DisconnectHandler = this.ReuseSession;
            session.CloseHandler = this.ReuseSession;

            session.BindSocket(socket);
            session.SetKeepAlive(this.KeepAlivePeriod);
            this.sessionManager.Add(session);
            return session;
        }


        /// <summary>
        /// 启动会话循环接收
        /// </summary>
        /// <param name="session">会话</param>
        private void StartLoopReceive(TcpSessionBase session)
        {
            // 通知插件会话已连接
            var context = this.CreateContext(session);
            if (this.plugManager.RaiseConnected(this, context) == false)
            {
                return;
            }

            if (session.IsSecurity == false)
            {
                session.StartLoopReceive();
                return;
            }

            try
            {
                session.SSLAuthenticate();
                if (this.plugManager.RaiseSSLAuthenticated(this, context, null))
                {
                    session.StartLoopReceive();
                }
            }
            catch (Exception ex)
            {
                this.plugManager.RaiseSSLAuthenticated(this, context, ex);
                this.ReuseSession(session);
            }
        }

        /// <summary>
        /// 收到请求数据
        /// </summary>
        /// <param name="session">会话对象</param>
        /// <returns></returns>
        private async Task OnRequestAsync(TcpSessionBase session)
        {
            try
            {
                var context = this.CreateContext(session);
                if (this.plugManager.RaiseRequested(this, context))
                {
                    await this.middlewareManager.Invoke(context);
                }
            }
            catch (Exception ex)
            {
                this.plugManager.RaiseException(this, ex);
            }
        }

        /// <summary>
        /// 回收复用会话对象
        /// 关闭会话并通知连接断开
        /// </summary>
        /// <param name="session">会话对象</param>
        private void ReuseSession(TcpSessionBase session)
        {
            if (this.sessionManager.Remove(session) == true)
            {
                var context = this.CreateContext(session);
                this.plugManager.RaiseDisconnected(this, context);
            }
        }

        /// <summary>
        /// 创建上下文对象
        /// </summary>
        /// <param name="session">当前会话</param>
        /// <returns></returns>
        private IContenxt CreateContext(TcpSessionBase session)
        {
            return new DefaultContext
            {
                Session = session,
                StreamReader = session.StreamReader,
                AllSessions = this.sessionManager
            };
        }

        #region IDisposable
        /// <summary>
        /// 获取对象是否已释放
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 关闭和释放所有相关资源
        /// </summary>
        public void Dispose()
        {
            if (this.IsDisposed == false)
            {
                this.Dispose(true);
                GC.SuppressFinalize(this);
            }
            this.IsDisposed = true;
        }

        /// <summary>
        /// 析构函数
        /// </summary>
        ~TcpListener()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        /// <param name="disposing">是否也释放托管资源</param>
        protected virtual void Dispose(bool disposing)
        {
            if (this.IsListening == true)
            {
                this.listenSocket.Dispose();
            }

            this.acceptArg.Dispose();
            this.sessionManager.Dispose();
            this.plugManager.Clear();
            this.middlewareManager.Clear();

            if (disposing == true)
            {
                this.listenSocket = null;
                this.acceptArg = null;
                this.plugManager = null;
                this.middlewareManager = null;
                this.sessionManager = null;

                this.LocalEndPoint = null;
                this.IsListening = false;
                this.KeepAlivePeriod = TimeSpan.Zero;
            }
        }
        #endregion

    }
}
