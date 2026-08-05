import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { Conexao, QrCode, SaudeConexao, StatusConexaoDto } from '../modelos';

/** O número de WhatsApp da empresa. Uma conexão por empresa na fase 1 — não há CRUD. */
@Injectable({ providedIn: 'root' })
export class ConexaoServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/conexao`;

  minha(): Observable<Conexao> {
    return this.http.get<Conexao>(this.base);
  }

  /** Estado ao vivo na Evolution. A tela chama em polling de 3s enquanto o QR está na frente
   *  do usuário — é assim que ela descobre que o pareamento deu certo. */
  status(): Observable<StatusConexaoDto> {
    return this.http.get<StatusConexaoDto>(`${this.base}/status`);
  }

  conectar(): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/conectar`, {});
  }

  parear(numero: string): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/parear`, { numero });
  }

  desconectar(): Observable<void> {
    return this.http.post<void>(`${this.base}/desconectar`, {});
  }

  reconhecerTroca(): Observable<void> {
    return this.http.post<void>(`${this.base}/reconhecer-troca`, {});
  }

  saude(): Observable<SaudeConexao> {
    return this.http.get<SaudeConexao>(`${this.base}/saude`);
  }
}
