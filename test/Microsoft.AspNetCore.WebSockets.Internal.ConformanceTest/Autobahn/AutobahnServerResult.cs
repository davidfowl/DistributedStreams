﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Newtonsoft.Json.Linq;

namespace Microsoft.AspNetCore.WebSockets.Internal.ConformanceTest.Autobahn
{
    public class AutobahnServerResult
    {
        public ServerType Server { get; }
        public bool Ssl { get; }
        public string Name { get; }
        public IEnumerable<AutobahnCaseResult> Cases { get; }

        public AutobahnServerResult(string name, IEnumerable<AutobahnCaseResult> cases)
        {
            Name = name;

            var splat = name.Split('|');
            if (splat.Length < 2)
            {
                throw new FormatException("Results incorrectly formatted");
            }

            Server = (ServerType)Enum.Parse(typeof(ServerType), splat[0]);
            Ssl = string.Equals(splat[1], "SSL", StringComparison.Ordinal);
            Cases = cases;
        }

        public static AutobahnServerResult FromJson(JProperty prop)
        {
            var valueObj = ((JObject)prop.Value);
            return new AutobahnServerResult(prop.Name, valueObj.Properties().Select(AutobahnCaseResult.FromJson));
        }
    }
}