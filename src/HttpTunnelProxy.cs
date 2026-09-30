using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Local HTTP CONNECT -> SSH SOCKS5 bridge. TLS stays end-to-end.
internal sealed class HttpTunnelProxy : IDisposable
{
    private readonly TcpListener listener;
    private readonly int socksPort;
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private volatile bool disposed;

    public HttpTunnelProxy(int httpPort, int socksPort)
    {
        this.socksPort = socksPort;
        listener = new TcpListener(IPAddress.Loopback, httpPort);
        listener.Start();
        var thread = new Thread(AcceptLoop) { IsBackground = true };
        thread.Start();
    }

    private void AcceptLoop()
    {
        while (!disposed)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                lock (clients)
                {
                    if (disposed) { client.Close(); return; }
                    clients.Add(client);
                }
                ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
            }
            catch { if (disposed) return; }
        }
    }

    private void Handle(TcpClient client)
    {
        TcpClient upstream = null;
        bool connected = false;
        try
        {
            client.ReceiveTimeout = client.SendTimeout = 15000;
            NetworkStream incoming = client.GetStream();
            var header = new StringBuilder();
            while (header.Length < 16384)
            {
                int next = incoming.ReadByte();
                if (next < 0) throw new IOException("Incomplete proxy request");
                header.Append((char)next);
                int length = header.Length;
                if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n' && header[length - 2] == '\r' && header[length - 1] == '\n') break;
            }
            string[] request = header.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Split(' ');
            Uri target;
            if (request.Length != 3 || request[0] != "CONNECT" || !Uri.TryCreate("https://" + request[1], UriKind.Absolute, out target) || target.Port != 443 || target.UserInfo.Length != 0)
            {
                Write(incoming, "HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                return;
            }
            byte[] host = Encoding.ASCII.GetBytes(target.IdnHost);
            if (host.Length == 0 || host.Length > 255) throw new IOException("Invalid hostname");
            upstream = new TcpClient();
            lock (clients)
            {
                if (disposed) return;
                clients.Add(upstream);
            }
            upstream.ReceiveTimeout = upstream.SendTimeout = 15000;
            upstream.Connect(IPAddress.Loopback, socksPort);
            NetworkStream outgoing = upstream.GetStream();
            outgoing.Write(new byte[] { 5, 1, 0 }, 0, 3);
            byte[] greeting = Read(outgoing, 2);
            if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS authentication failed");
            byte[] connect = new byte[host.Length + 7];
            connect[0] = 5; connect[1] = 1; connect[3] = 3; connect[4] = (byte)host.Length;
            Buffer.BlockCopy(host, 0, connect, 5, host.Length);
            connect[connect.Length - 2] = 1; connect[connect.Length - 1] = 187; // 443
            outgoing.Write(connect, 0, connect.Length);
            byte[] reply = Read(outgoing, 4);
            if (reply[0] != 5 || reply[1] != 0) throw new IOException("SOCKS destination unavailable");
            if (reply[3] == 1) Read(outgoing, 6);
            else if (reply[3] == 4) Read(outgoing, 18);
            else if (reply[3] == 3) Read(outgoing, Read(outgoing, 1)[0] + 2);
            else throw new IOException("Invalid SOCKS reply");
            Write(incoming, "HTTP/1.1 200 Connection Established\r\n\r\n");
            connected = true;
            client.ReceiveTimeout = client.SendTimeout = upstream.ReceiveTimeout = upstream.SendTimeout = 0;
            Task toServer = incoming.CopyToAsync(outgoing);
            Task toClient = outgoing.CopyToAsync(incoming);
            Task.WaitAny(toServer, toClient);
            client.Close(); upstream.Close();
            try { Task.WaitAll(toServer, toClient); } catch { }
        }
        catch
        {
            if (!connected) try { Write(client.GetStream(), "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"); } catch { }
        }
        finally
        {
            client.Close();
            if (upstream != null) upstream.Close();
            lock (clients) { clients.Remove(client); if (upstream != null) clients.Remove(upstream); }
        }
    }

    private static byte[] Read(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0) throw new IOException("SOCKS connection closed");
            offset += read;
        }
        return buffer;
    }

    private static void Write(Stream stream, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        disposed = true;
        listener.Stop();
        lock (clients) { foreach (TcpClient client in clients) client.Close(); clients.Clear(); }
    }
}
