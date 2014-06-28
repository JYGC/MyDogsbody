require 'net/https'
require 'net/http'
require 'rubygems'
require 'nokogiri'

class URLHandling
  
  def self.urlSchemeParse(url)
    http = Net::HTTP.new(url.host,url.port)
    urlscheme = url.class
    
    puts "#{urlscheme}"
    
    if "#{urlscheme}" == "URI::HTTP"
      http.use_ssl = false
    elsif "#{urlscheme}" == "URI::HTTPS"
      http.use_ssl = true
    elsif "#{urlscheme}" == "URI::FTP"
      puts "FTP support not implemented yet"
    end
    
    return urlscheme, http
  end
  
  def self.fixMalformURL(pageurl)
    # if statement here
    newpageurl = nil
    return newpageurl
  end
  
end

class ConnHandling
  
  def self.requestResponse(url,http)
    request = Net::HTTP::Get.new(url.request_uri)
    response = http.request(request)
    
    return response
  end
  
end