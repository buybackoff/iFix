using iFix.Core;
using System.IO;

namespace iFix.Mantle
{
    // This class doesn't do much. It exists primarily to avoid exposing Core from Mantle
    // and for parallelism with Receiver.
    public class Publisher
    {
        // Does not flush.
        public static void Publish(Stream output, string version, IMessage msg)
        {
            Serialization.WriteMessage(output, Serialization.SerializeString(version), msg.Fields);
        }
    }
}
