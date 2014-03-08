#!/usr/bin/perl

# Copyright (c) 2013, 2014, Junying Chen <casperchen91@hotmail.com>
#
# Permission to use, copy, modify, and/or distribute this
# software for any purpose with or without fee is hereby
# granted, provided that the above copyright notice and this
# permission notice appear in all copies.
#
# THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS
# ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL
# IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO
# EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
# INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
# WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
# WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
# TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE
# USE OR PERFORMANCE OF THIS SOFTWARE.

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
