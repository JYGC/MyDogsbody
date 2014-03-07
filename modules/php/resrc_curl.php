<?php
$ch = curl_init();

while($url = trim(fgets(STDIN))){
	try{
		curl_setopt($ch, CURLOPT_URL, $url);
		curl_setopt($ch, CURLOPT_FOLLOWLOCATION, 1);
		curl_setopt($ch, CURLOPT_HEADER, 0);
		curl_setopt($ch, CURLOPT_RETURNTRANSFER, 1);

		$html_content = curl_exec($ch);

		echo $html_content;
	}catch(Exception $e){
		echo "Error.";
	}
}

curl_close($ch);
?>
