#!/usr/bin/env perl
use v5.36;
use JSON::PP qw(decode_json);

my $notices = 'THIRD_PARTY_NOTICES.md';

# analyzers and native AOT toolchain run at build time and are not part of the executable
my %excluded = map { $_ => 1 } qw(
  Meziantou.Analyzer
  Roslynator.Analyzers
  Microsoft.DotNet.ILCompiler
  Microsoft.NET.ILLink.Tasks
);

my %shipped = map { $_ => 1 } grep { !$excluded{$_} } shipped_packages();
my %listed  = map { $_ => 1 } listed_packages($notices);
die "Found no packages to compare.\n" unless %shipped && %listed;

my @missing = sort grep { !$listed{$_} } keys %shipped;
my @extra   = sort grep { !$shipped{$_} } keys %listed;
warn "Missing from $notices: $_\n"              for @missing;
warn "Listed in $notices but not shipped: $_\n" for @extra;
exit 1 if @missing || @extra;

say "$notices lists all ", scalar keys %shipped, ' shipped packages.';

sub shipped_packages {
    my $json =
qx(dotnet list src/RoslynBridge package --include-transitive --format json);
    die "dotnet list failed\n" if $?;

    return map { $_->{id} }
      map {
        ( $_->{topLevelPackages}->@*, ( $_->{transitivePackages} // [] )->@* )
      }
      map { $_->{frameworks}->@* } decode_json($json)->{projects}->@*;
}

# each package is a "- `Id`: ..." line under the "NuGet packages" heading
sub listed_packages ($path) {
    open my $file, '<', $path or die "$path: $!\n";
    my @lines = <$file>;
    close $file;

    my ( $in_packages, @ids );
    for (@lines) {
        $in_packages = /^## NuGet packages\s*$/ if /^## /;
        push @ids, $1 if $in_packages && /^- `([^`]+)`/;
    }

    return @ids;
}
