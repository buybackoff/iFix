using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace iFix.Common
{
    class InternalErrorException : Exception
    {
        public InternalErrorException(string msg) : base(msg) { }
    }

    public class Assert
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NotNull<T>(T obj) where T : class
        {
            if (obj == null)
                throw new InternalErrorException("Unexpected null object");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NotNull<T>(T obj, string format, params object[] args) where T : class
        {
            if (obj == null)
                throw new InternalErrorException(String.Format("Unexpected null object. " + format, args));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void True(bool expr)
        {
            if (!expr)
                throw new InternalErrorException("Assertion violation");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void True(bool expr, string format, params object[] args)
        {
            if (!expr)
                throw new InternalErrorException(String.Format("Assertion violation. " + format, args));
        }
    }
}
