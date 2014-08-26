using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iFix.Crust
{
    public class InitializingConnector : IConnector
    {
        IConnector _connector;
        Action<IConnection, CancellationToken> _initialize;

        public InitializingConnector(IConnector connector, Action<IConnection, CancellationToken> initialize)
        {
            _connector = connector;
            _initialize = initialize;
        }

        public async Task<IConnection> CreateConnection(CancellationToken cancellationToken)
        {
            IConnection res = await _connector.CreateConnection(cancellationToken);
            try
            {
                _initialize.Invoke(res, cancellationToken);
            }
            catch (Exception)
            {
                res.Dispose();
                throw;
            }
            return res;
        }
    }
}
