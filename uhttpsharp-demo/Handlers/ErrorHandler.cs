﻿/*
 * Copyright (C) 2011 uhttpsharp project - http://github.com/raistlinthewiz/uhttpsharp
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.

 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.

 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */

using System.Threading.Tasks;
using uhttpsharp;

namespace uhttpsharpdemo.Handlers
{
    public class ErrorHandler : IHttpRequestHandler
    {
        public Task Handle(IHttpContext context, System.Func<Task> next)
        {
            context.Response = new HttpResponse(HttpResponseCode.NotFound, "These are not the droids you are looking for.", true);
            return Task.CompletedTask;
        }
    }
}