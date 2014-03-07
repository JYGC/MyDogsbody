#!/usr/bin/perl

use URI::Find;


my @uris;
my $finder = URI::Find->new(sub {
	my $uri = shift;
	push(@uris, $uri);
} );

my $buffer = do{ local $/; <STDIN> };
$finder->find(\$buffer);

foreach $link (@uris){
	print "$link\n";
}