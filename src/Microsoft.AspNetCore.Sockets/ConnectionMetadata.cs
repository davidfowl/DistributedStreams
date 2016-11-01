﻿
using System.Collections.Generic;

namespace Microsoft.AspNetCore.Sockets
{
    public class ConnectionMetadata
    {
        private IDictionary<string, object> _metadata = new Dictionary<string, object>();

        public Format Format { get; set; } = Format.Text;

        public object this[string key]
        {
            get
            {
                object value;
                _metadata.TryGetValue(key, out value);
                return value;
            }
            set
            {
                _metadata[key] = value;
            }
        }
    }
}
