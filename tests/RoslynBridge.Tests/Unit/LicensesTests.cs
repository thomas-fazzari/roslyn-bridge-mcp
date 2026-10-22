// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace RoslynBridge.Tests.Unit;

public sealed class LicensesTests
{
    [Fact]
    public void PrintWritesTheEmbeddedNotices()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        Licenses.Print(output);

        output
            .ToString()
            .Should()
            .Contain("Copyright (C) 2026 Thomas Fazzari")
            .And.Contain("GNU Affero General Public License")
            .And.Contain("# Third-party notices")
            .And.Contain("License notice for");
    }
}
