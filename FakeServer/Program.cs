using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class Program
{
    public static int Main(String[] args)
    {
        // Data buffer for incoming data.
        byte[] bytes = new Byte[1 << 20];

        IPEndPoint localEndPoint = new IPEndPoint(new IPAddress(new byte[] { 127, 0, 0, 1 }), 5001);
        Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            listener.Bind(localEndPoint);
            listener.Listen(10);

            // Start listening for connections.
            while (true)
            {
                Console.WriteLine("Waiting for a connection...");
                Socket handler = listener.Accept();
                Console.WriteLine("Connected");

                try
                {
                    while (true)
                    {
                        handler.Receive(bytes);
                    }
                }
                catch (Exception) { }

                Console.WriteLine("Connection terminated");
                try { handler.Shutdown(SocketShutdown.Both); }
                catch (Exception) { }
                try { handler.Close(); }
                catch (Exception) { }
                try { handler.Dispose(); }
                catch (Exception) { }
            }

        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
        return 0;
    }
}