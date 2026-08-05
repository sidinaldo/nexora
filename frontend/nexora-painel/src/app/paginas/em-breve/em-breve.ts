import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/** Rota placeholder das telas de domínio que ainda não existem (Funil, Contatos, Meu Dia).
 *
 *  Existe para a sidebar não ter link morto: clicar e cair num 404 é pior que clicar e ler
 *  "ainda não disponível". O título vem do `data` da rota. */
@Component({
  selector: 'app-em-breve',
  standalone: true,
  template: `
    <div class="pagina">
      <h1>{{ titulo }}</h1>
      <div class="cartao">
        <div class="vazio">
          Esta tela ainda não está disponível.<br />
          <span class="fraco">Enquanto isso, use a Caixa de Entrada para atender.</span>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .pagina { padding: 22px; }
    .cartao { margin-top: 16px; }
  `]
})
export class EmBreve {
  titulo = inject(ActivatedRoute).snapshot.data['titulo'] ?? 'Em breve';
}
