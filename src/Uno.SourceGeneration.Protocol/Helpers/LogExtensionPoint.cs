// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
using System;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Uno.SourceGeneratorTasks.Helpers
{
    public static class LogExtensionPoint
    {
        private static ILoggerFactory _loggerFactory;
		private static object _gate = new object();

        private static class Container<T>
        {
            internal static readonly ILogger Logger = AmbientLoggerFactory.CreateLogger<T>();
        }

		/// <summary>
		/// Retreives the <see cref="ILoggerFactory"/> for this the Uno extension point.
		/// </summary>
		public static ILoggerFactory AmbientLoggerFactory
		{
			get
			{
				if (_loggerFactory == null)
				{
					lock (_gate)
					{
						if (_loggerFactory == null)
						{
							_loggerFactory = new LoggerFactory();
						}
					}
				}

				return _loggerFactory;
			}
		}

		/// <summary>
		/// Gets a <see cref="ILogger"/> for the specified type.
		/// </summary>
		/// <param name="forType"></param>
		/// <returns></returns>
		public static ILogger Log(this Type forType) 
			=> AmbientLoggerFactory.CreateLogger(forType);

		/// <summary>
		/// Gets a logger instance for the current types
		/// </summary>
		/// <typeparam name="T">The type for which to get the logger</typeparam>
		/// <param name="instance"></param>
		/// <returns>A logger for the type of the instance</returns>
		public static ILogger Log<T>(this T instance) => Container<T>.Logger;
	}
}
