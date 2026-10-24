#!/usr/bin/env perl
use strict;
use warnings;

my $types = 'fix|feat|test|style|docs|build|ops|chore|perf|refactor';
open my $file, '<', $ARGV[0] or die "$ARGV[0]: $!\n";
my @lines;
while (<$file>) {
    last if /^# -+ >8 -+$/;
    push @lines, $_ unless /^#/;
}
close $file;
chomp @lines;
my $subject = shift @lines // '';

# Git writes these itself, and a squash merge takes its title from the pull request
exit 0 if $subject =~ /^(?:Merge|Revert|fixup!|squash!) /;
die
"Start the commit message with a type ($types), then ': ', in lowercase. Add ! after the type for a breaking change.\n"
  unless $subject =~ /^(?:$types)!?: [^A-Z\s]/;
die 'Keep the first line within 50 characters. It has '
  . length($subject) . ".\n"
  if length $subject > 50;
die
"Write only a title. Put the description and any trailer in the pull request.\n"
  if grep { /\S/ } @lines;
