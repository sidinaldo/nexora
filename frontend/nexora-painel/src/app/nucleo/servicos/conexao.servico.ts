import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { Conexao, Conexoes, QrCode, SaudeConexao, StatusConexaoDto } from '../modelos';

/** Os números de WhatsApp da empresa. Quantos ela pode ter vem do plano, e o servidor é quem
 *  diz — ver `Conexoes.limite`. */
@Injectable({ providedIn: 'root' })
export class ConexaoServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/conexoes`;

  listar(): Observable<Conexoes> {
    return this.http.get<Conexoes>(this.base);
  }

  obter(id: number): Observable<Conexao> {
    return this.http.get<Conexao>(`${this.base}/${id}`);
  }

  criar(nome: string): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, { nome });
  }

  /** Só o nome. `instanceName` não tem rota de edição em lugar nenhum, de propósito: é a
   *  identidade na Evolution e a chave pela qual o webhook acha o tenant. */
  renomear(id: number, nome: string): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, { nome });
  }

  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  /** Estado ao vivo na Evolution. A tela chama em polling de 3s enquanto o QR está na frente
   *  do usuário — é assim que ela descobre que o pareamento deu certo. */
  status(id: number): Observable<StatusConexaoDto> {
    return this.http.get<StatusConexaoDto>(`${this.base}/${id}/status`);
  }

  conectar(id: number): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/${id}/conectar`, {});
  }

  parear(id: number, numero: string): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/${id}/parear`, { numero });
  }

  desconectar(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/desconectar`, {});
  }

  reconhecerTroca(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reconhecer-troca`, {});
  }

  saude(id: number): Observable<SaudeConexao> {
    return this.http.get<SaudeConexao>(`${this.base}/${id}/saude`);
  }
}
