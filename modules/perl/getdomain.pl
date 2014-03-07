#!/usr/bin/perl

use URI;

my $url		= URI->new( <STDIN> );
my $domain	= $url->host;

print $domain;