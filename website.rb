#!/usr/bin/env ruby

# Copyright (c) 2014, Junying Chen <casperchen91@hotmail.com>
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

require 'net/http'
require 'rubygems'
require 'nokogiri'
require 'open-uri'

require_relative 'auxillary'

class Website
  
  # Class variables
  @@webSiteName = nil
  @@webSiteDocument = nil
  @@tovisit = Array.new # array.unshift([pageurl,document])
  @@visited = Array.new #
  
  # Website's constructor
  def initialize(websitename)
    @@webSiteName = websitename
    @@tovisit.unshift({
      'pageurl'  => @@webSiteName,
      'urlscheme'=> nil,
      'header'   => nil,
      'document' => nil})
  end
  
  def crawlSite
    while 0 != @@tovisit.length do
      page = @@tovisit.pop
      if page['pageurl']
        @@visited.unshift(openLinks(page))
      end
    end
  end
  
  def getWebSiteName
    return @@webSiteName
  end
  
  def getTovisit
    return @@tovisit
  end
  
  def getVisited
    return @@visited
  end
  
  def openLinks(page)
    puts "#{page['pageurl']}" #####
    url = URI.parse(URI.encode(page['pageurl'].strip))
    
    urlscheme, http = URLHandling.urlSchemeParse(url)
    page['urlscheme'] = urlscheme
    
    if url.respond_to?(:request_uri)
      response = ConnHandling.requestResponse(url, http)
      page['header'] = response.header
      addPageToList(page['header'][
        'location']) if page['header']['location']
      page['document'] = Nokogiri::HTML(response.body)
      links = page['document'].css('a').map{ |link|
        link['href']}
      links.each { |link|
        addPageToList(link)
      }
    end
    return page
  end
  
  def addPageToList(pageurl)
    if !@@tovisit.any? {|page| page['pageurl'] == pageurl} &&
        !@@visited.any? {|page| page['pageurl'] == pageurl}
      @@tovisit.unshift({
        'pageurl' => pageurl,
      'urlscheme'=> nil,
      'header'   => nil,
      'document' => nil})
    end
  end
  
end