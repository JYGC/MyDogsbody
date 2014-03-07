#!/usr/bin/perl

use strict;
use warnings;


my $stdin_buffer;
my $stdin_buffer_ftp;
my $stdin_buffer_http;
my $stdin_buffer_https;
my $link;

while($stdin_buffer = <STDIN>){

	$stdin_buffer	=~ s@(?<!http://)(www\.\S+[^.,!? ])@http://$1@g;

	while($stdin_buffer =~ m@(((http://)|(https://)|(ftp://))\S+[^.,!? ])@g){
		$link = $1;
		$link =~ s/\'.*//;
		$link =~ s/\".*//;
		$link =~ s/\).*//;
		$link =~ s/\(.*//;
		print $link, "\n";
	}
}
